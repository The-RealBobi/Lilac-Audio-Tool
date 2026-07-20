#!/usr/bin/env python3
"""Small CRI ACB/AWB inspection and AWB replacement tool.

This is intentionally conservative: source files are never modified in place,
and write support stays limited to AFS2/AWB repacking.
"""

from __future__ import annotations

import argparse
import binascii
import gzip
import hashlib
import json
import os
import platform
import shutil
import struct
import subprocess
import tarfile
import tempfile
import urllib.request
import wave
import zipfile
from dataclasses import dataclass
from pathlib import Path
from typing import Any


ACB_MAGIC = b"@UTF"
AWB_MAGIC = b"AFS2"
USM_MAGIC = b"CRID"
HCA_MAGIC = b"HCA\x00"
ENCRYPTED_HCA_MAGIC = bytes([0xC8, 0xC3, 0xC1, 0x00])
FFMPEG_RELEASE_BASE = "https://github.com/eugeneware/ffmpeg-static/releases/download/b6.0"
VGMSTREAM_RELEASE_API = "https://api.github.com/repos/vgmstream/vgmstream-releases/releases/latest"
FFMPEG_ASSETS = {
    "darwin-arm64": ("ffmpeg-darwin-arm64.gz", "6be74d6f449889c2e87a75873894f8520cad56c08ac76f2a628d85b0519daaca"),
    "linux-x64": ("ffmpeg-linux-x64.gz", "17c1ae10b52ac499180679fe6ba77e17642390c4eedb0f1e3b0ac045da55128f"),
    "win-x64": ("ffmpeg-win32-x64.gz", "450d66226c79405c724e821f291cab0911e934bfa9fa2231adcab587f3e07b50"),
}


def align(value: int, alignment: int) -> int:
    if alignment <= 1:
        return value
    return (value + alignment - 1) & ~(alignment - 1)


def read_cstring(data: bytes, offset: int) -> str:
    if offset < 0 or offset >= len(data):
        return ""
    end = data.find(b"\x00", offset)
    if end < 0:
        end = len(data)
    return data[offset:end].decode("utf-8", errors="replace")


def detect_extension(data: bytes) -> str:
    if data.startswith(HCA_MAGIC) or data.startswith(ENCRYPTED_HCA_MAGIC):
        return ".hca"
    if data.startswith(b"RIFF"):
        return ".wav"
    if data[:1] == b"\x80":
        return ".adx"
    return ".bin"


def crc16_hca(data: bytes | bytearray) -> int:
    value = 0
    for byte in data:
        value ^= byte << 8
        for _ in range(8):
            if value & 0x8000:
                value = ((value << 1) ^ 0x8005) & 0xFFFF
            else:
                value = (value << 1) & 0xFFFF
    return value


def hca_type1_decryption_table() -> bytes:
    table = bytearray(256)
    value = 0
    output = 1
    for _ in range(256):
        value = (value * 13 + 11) % 256
        if value not in (0, 255):
            table[output] = value
            output += 1
    table[255] = 255
    return bytes(table)


def find_hca_chunk(data: bytes | bytearray, chunk: bytes, header_size: int) -> int:
    for offset in range(8, max(8, header_size - len(chunk) + 1)):
        if data[offset:offset + len(chunk)] == chunk:
            return offset
    return -1


def normalize_hca_type1_to_plain(path: Path) -> bool:
    data = path.read_bytes()
    if not data.startswith(HCA_MAGIC):
        return False

    output = bytearray(data)
    header_size = struct.unpack_from(">H", output, 6)[0]
    fmt_offset = find_hca_chunk(output, b"fmt\x00", header_size)
    comp_offset = find_hca_chunk(output, b"comp", header_size)
    ciph_offset = find_hca_chunk(output, b"ciph", header_size)
    if fmt_offset < 0 or comp_offset < 0 or ciph_offset < 0:
        return False

    encryption_type = struct.unpack_from(">H", output, ciph_offset + 4)[0]
    if encryption_type == 0:
        return False
    if encryption_type != 1:
        raise ValueError(f"Unsupported HCA EncryptionType={encryption_type}")

    frame_count = struct.unpack_from(">I", output, fmt_offset + 8)[0]
    frame_size = struct.unpack_from(">H", output, comp_offset + 4)[0]
    table = hca_type1_decryption_table()
    audio_offset = header_size
    for frame_index in range(frame_count):
        start = audio_offset + frame_index * frame_size
        end = start + frame_size
        if end > len(output):
            raise ValueError("Truncated HCA frame data")
        frame = bytearray(table[value] for value in output[start:end])
        struct.pack_into(">H", frame, frame_size - 2, crc16_hca(frame[:-2]))
        output[start:end] = frame

    struct.pack_into(">H", output, ciph_offset + 4, 0)
    struct.pack_into(">H", output, header_size - 2, crc16_hca(output[:header_size - 2]))
    path.write_bytes(output)
    return True


def match_hca_header_profile(path: Path, original_hca: dict[str, Any]) -> bool:
    original_body = original_hca.get("Hca") or {}
    original_version = int(original_hca.get("Version") or 0)
    original_min_resolution = original_body.get("MinResolution")
    if original_version <= 0 and original_min_resolution is None:
        return False

    data = bytearray(path.read_bytes())
    if not data.startswith(HCA_MAGIC):
        return False

    changed = False
    if original_version > 0:
        current_version = struct.unpack_from(">H", data, 4)[0]
        if current_version != original_version:
            struct.pack_into(">H", data, 4, original_version)
            changed = True

    if original_min_resolution is not None:
        header_size = struct.unpack_from(">H", data, 6)[0]
        comp_offset = find_hca_chunk(data, b"comp", header_size)
        if comp_offset >= 0:
            min_resolution_offset = comp_offset + 6
            min_resolution = int(original_min_resolution)
            if 0 <= min_resolution <= 0xFF and data[min_resolution_offset] != min_resolution:
                data[min_resolution_offset] = min_resolution
                changed = True

    if changed:
        path.write_bytes(data)
    return changed


def crc32_filename_key(filename: str) -> int:
    return binascii.crc32(filename.encode("utf-8")) & 0xFFFFFFFF


def decrypt_readable_copy(data: bytes, filename: str) -> bytes:
    """Apply the CRI readable-copy XOR when needed."""
    expected_magic = {
        ".acb": ACB_MAGIC,
        ".awb": AWB_MAGIC,
        ".usm": USM_MAGIC,
    }.get(Path(filename).suffix.lower())
    if expected_magic is None or data.startswith(expected_magic):
        return data

    key = crc32_filename_key(Path(filename).name)
    key_bytes = struct.pack("<I", key)
    output = bytearray(data)

    def update_crc_state(seed: int) -> int:
        crc = (~seed) & 0xFFFFFFFF
        for value in key_bytes:
            index = (crc ^ value) & 0xFF
            crc = ((crc >> 8) ^ CRC32_TABLE[index]) & 0xFFFFFFFF
        return (~crc) & 0xFFFFFFFF

    current_crc = update_crc_state(0)
    for i in range(len(output)):
        if (i & 3) == 0:
            current_crc = update_crc_state(i & -4)

        base_shift = (i & 3) * 2
        mask_bits = (current_crc >> (base_shift + 8)) & 3
        value = (current_crc >> base_shift) & 0xFF
        mask_bits |= (value << 2) & 0xFF
        value = (current_crc >> (base_shift + 16)) & 3
        mask_bits = ((mask_bits << 2) & 0xFF) | value
        value = (current_crc >> (base_shift + 24)) & 3
        mask_bits = ((mask_bits << 2) & 0xFF) | value
        output[i] ^= mask_bits

    readable = bytes(output)
    if not readable.startswith(expected_magic):
        raise ValueError(f"Could not decrypt/read {filename} as CRI {expected_magic!r}")
    return readable


def build_crc32_table() -> list[int]:
    table: list[int] = []
    for i in range(256):
        value = i
        for _ in range(8):
            if value & 1:
                value = (value >> 1) ^ 0xEDB88320
            else:
                value >>= 1
        table.append(value & 0xFFFFFFFF)
    return table


CRC32_TABLE = build_crc32_table()


@dataclass(frozen=True)
class AwbEntry:
    index: int
    id: int
    offset: int
    data: bytes

    @property
    def size(self) -> int:
        return len(self.data)

    @property
    def extension(self) -> str:
        return detect_extension(self.data)


@dataclass(frozen=True)
class AwbArchive:
    version: int
    reserved: int
    offset_size: int
    id_size: int
    alignment: int
    subkey: int
    entries: tuple[AwbEntry, ...]


def read_int_le(data: bytes, offset: int, size: int) -> int:
    if size == 2:
        return struct.unpack_from("<H", data, offset)[0]
    if size == 4:
        return struct.unpack_from("<I", data, offset)[0]
    if size == 8:
        return struct.unpack_from("<Q", data, offset)[0]
    raise ValueError(f"Unsupported integer size: {size}")


def write_int_le(value: int, size: int) -> bytes:
    if size == 2:
        return struct.pack("<H", value)
    if size == 4:
        return struct.pack("<I", value)
    if size == 8:
        return struct.pack("<Q", value)
    raise ValueError(f"Unsupported integer size: {size}")


def parse_awb(data: bytes) -> AwbArchive:
    if len(data) < 0x10 or not data.startswith(AWB_MAGIC):
        raise ValueError("Not a valid AFS2/AWB archive")

    version = data[4]
    offset_size = data[5]
    id_size = data[6]
    reserved = data[7]
    file_count = struct.unpack_from("<I", data, 8)[0]
    alignment = struct.unpack_from("<H", data, 12)[0]
    subkey = struct.unpack_from("<H", data, 14)[0]

    cursor = 0x10
    ids = []
    for _ in range(file_count):
        ids.append(read_int_le(data, cursor, id_size))
        cursor += id_size

    offsets = []
    for _ in range(file_count + 1):
        offsets.append(read_int_le(data, cursor, offset_size))
        cursor += offset_size

    entries: list[AwbEntry] = []
    for index, entry_id in enumerate(ids):
        start = align(offsets[index], alignment)
        end = offsets[index + 1]
        if 0 <= start <= end <= len(data):
            entries.append(AwbEntry(index, entry_id, start, data[start:end]))

    return AwbArchive(version, reserved, offset_size, id_size, alignment, subkey, tuple(entries))


def choose_offset_size(original_size: int, max_offset: int) -> int:
    if original_size == 2 and max_offset <= 0xFFFF:
        return 2
    if original_size in (2, 4) and max_offset <= 0xFFFFFFFF:
        return 4
    return 8


def build_awb(original: AwbArchive, entries: list[AwbEntry]) -> bytes:
    max_id = max((entry.id for entry in entries), default=0)
    id_size = original.id_size if original.id_size == 4 or max_id <= 0xFFFF else 4

    offset_size = original.offset_size
    while True:
        header_size = 0x10 + len(entries) * id_size + (len(entries) + 1) * offset_size
        cursor = header_size
        offsets = [cursor]
        for entry in entries:
            cursor = align(cursor, original.alignment)
            cursor += len(entry.data)
            offsets.append(cursor)

        needed_offset_size = choose_offset_size(original.offset_size, cursor)
        if needed_offset_size == offset_size:
            break
        offset_size = needed_offset_size

    output = bytearray()
    output += AWB_MAGIC
    output += bytes([original.version, offset_size, id_size, original.reserved])
    output += struct.pack("<IHH", len(entries), original.alignment, original.subkey)
    for entry in entries:
        output += write_int_le(entry.id, id_size)
    for value in offsets:
        output += write_int_le(value, offset_size)

    first_data_start = align(offsets[0], original.alignment)
    if len(output) > first_data_start:
        raise ValueError("AWB header overlaps data region")
    output += b"\x00" * (first_data_start - len(output))

    for index, entry in enumerate(entries):
        start = align(len(output), original.alignment)
        if len(output) < start:
            output += b"\x00" * (start - len(output))
        expected_start = align(offsets[index], original.alignment)
        if start != expected_start:
            raise ValueError("Internal AWB offset calculation mismatch")
        output += entry.data

    return bytes(output)


def awb_metadata(archive: AwbArchive) -> dict[str, Any]:
    return {
        "type": "awb",
        "file_count": len(archive.entries),
        "version": archive.version,
        "reserved": archive.reserved,
        "offset_size": archive.offset_size,
        "id_size": archive.id_size,
        "alignment": archive.alignment,
        "subkey": archive.subkey,
        "entries": [
            {
                "index": entry.index,
                "id": entry.id,
                "offset": entry.offset,
                "size": entry.size,
                "extension": entry.extension,
                "sha1": hashlib.sha1(entry.data).hexdigest(),
            }
            for entry in archive.entries
        ],
    }


def cue_display_name(row: list["UtfField"], cue_index: int) -> str:
    for name in ("Name", "CueName", "CueNameTable", "UserData"):
        field = field_by_name(row, name)
        if field is not None and isinstance(field.value, str) and field.value:
            return field.value
    return f"Cue {cue_index}"


def acb_awb_clip_name_lists(acb_data: bytes) -> dict[int, list[str]]:
    table = parse_utf(acb_data)
    nested = table.nested_tables()
    waveform_table = nested.get("WaveformTable")
    cue_table = nested.get("CueTable")
    if waveform_table is None or cue_table is None:
        return {}

    sequence_table = nested.get("SequenceTable")
    synth_table = nested.get("SynthTable")
    track_table = nested.get("TrackTable")
    track_event_table = nested.get("TrackEventTable")
    sequence_command_table = nested.get("SeqCommandTable")
    cue_name_table = nested.get("CueNameTable")
    cue_names_by_index: dict[int, str] = {}
    if cue_name_table is not None:
        for row in cue_name_table.rows:
            cue_index = int_value(row, "CueIndex")
            cue_name = field_by_name(row, "CueName")
            if cue_index is not None and isinstance(cue_name.value if cue_name is not None else None, str):
                cue_names_by_index[cue_index] = cue_name.value

    names_by_awb_id: dict[int, list[str]] = {}
    for cue_index, cue_row in enumerate(cue_table.rows):
        cue_name = cue_names_by_index.get(cue_index) or cue_display_name(cue_row, cue_index)
        for waveform_index in resolve_cue_waveforms(cue_row, waveform_table, sequence_table, synth_table, track_table, track_event_table, sequence_command_table):
            if waveform_index < 0 or waveform_index >= len(waveform_table.rows):
                continue

            waveform_row = waveform_table.rows[waveform_index]
            awb_id = int_value(waveform_row, "StreamAwbId")
            if awb_id is None or awb_id < 0:
                awb_id = int_value(waveform_row, "MemoryAwbId")
            if awb_id is None or awb_id < 0:
                continue

            names = names_by_awb_id.setdefault(awb_id, [])
            if cue_name not in names:
                names.append(cue_name)

    return names_by_awb_id


def acb_awb_clip_names(acb_data: bytes) -> dict[int, str]:
    return {
        awb_id: (names[0] if names else "")
        for awb_id, names in acb_awb_clip_name_lists(acb_data).items()
    }


def acb_awb_entry_info(acb_data: bytes, awb_id: int) -> dict[str, Any] | None:
    table = parse_utf(acb_data)
    nested = table.nested_tables()
    waveform_table = nested.get("WaveformTable")
    if waveform_table is None:
        return None

    extension_table = nested.get("WaveformExtensionDataTable")
    for waveform_index, row in enumerate(waveform_table.rows):
        stream_id = int_value(row, "StreamAwbId")
        memory_id = int_value(row, "MemoryAwbId")
        if stream_id != awb_id and memory_id != awb_id:
            continue

        loop_start = None
        loop_end = None
        loop_source = None
        extension_index = int_value(row, "ExtensionData")
        if (
            extension_table is not None and
            extension_index is not None and
            extension_index != 0xFFFF and
            0 <= extension_index < len(extension_table.rows)
        ):
            extension_row = extension_table.rows[extension_index]
            loop_start = int_value(extension_row, "LoopStart")
            loop_end = int_value(extension_row, "LoopEnd")
            if loop_start is not None and loop_end is not None and loop_end > loop_start:
                loop_source = "acb"

        loop_flag = int_value(row, "LoopFlag")
        if loop_flag is not None and loop_flag != 2:
            loop_start = None
            loop_end = None
            loop_source = None

        return {
            "waveform_index": waveform_index,
            "stream_awb_id": stream_id,
            "memory_awb_id": memory_id,
            "loop_flag": loop_flag,
            "sample_count": int_value(row, "NumSamples"),
            "sample_rate": int_value(row, "SamplingRate"),
            "channels": int_value(row, "NumChannels"),
            "encode_type": int_value(row, "EncodeType"),
            "loop": {
                "start": loop_start,
                "end": loop_end,
                "source": loop_source,
            } if loop_source is not None else None,
        }

    return None


class UtfField:
    def __init__(self, name: str, raw_type: int, value: Any, offset: int, size: int):
        self.name = name
        self.raw_type = raw_type
        self.value = value
        self.offset = offset
        self.size = size

    @property
    def kind(self) -> str:
        type_id = self.raw_type & 0x0F
        if type_id == 0x0B:
            return "data"
        if type_id == 0x0A:
            return "string"
        if type_id == 0x08:
            return "float"
        return "number"

    def to_json(self) -> dict[str, Any]:
        if self.kind == "data":
            value = f"<{len(self.value or b'')} bytes>"
        else:
            value = self.value
        return {
            "name": self.name,
            "type": self.kind,
            "value": value,
            "offset": self.offset,
            "size": self.size,
        }


class UtfTable:
    def __init__(self, name: str, rows: list[list[UtfField]]):
        self.name = name
        self.rows = rows

    def nested_tables(self) -> dict[str, "UtfTable"]:
        nested: dict[str, UtfTable] = {}
        for row in self.rows:
            for field in row:
                if field.kind == "data" and isinstance(field.value, bytes) and field.value.startswith(ACB_MAGIC):
                    try:
                        nested[field.name] = parse_utf_at(field.value, field.offset)
                    except ValueError:
                        pass
        return nested

    def to_json(self, include_rows: bool = True) -> dict[str, Any]:
        payload: dict[str, Any] = {
            "table_name": self.name,
            "row_count": len(self.rows),
            "columns": [field.name for field in self.rows[0]] if self.rows else [],
        }
        if include_rows:
            payload["rows"] = [
                {"fields": [field.to_json() for field in row]}
                for row in self.rows
            ]
        return payload


def parse_utf(data: bytes) -> UtfTable:
    return parse_utf_at(data, 0)


def parse_utf_at(data: bytes, base_offset: int) -> UtfTable:
    if len(data) < 0x20 or not data.startswith(ACB_MAGIC):
        raise ValueError("Not a valid CRI @UTF table")

    row_offset = struct.unpack_from(">H", data, 0x0A)[0] + 8
    string_table_offset = struct.unpack_from(">I", data, 0x0C)[0] + 8
    data_offset = struct.unpack_from(">I", data, 0x10)[0] + 8
    table_name_offset = struct.unpack_from(">I", data, 0x14)[0]
    field_count = struct.unpack_from(">H", data, 0x18)[0]
    row_size = struct.unpack_from(">H", data, 0x1A)[0]
    row_count = struct.unpack_from(">I", data, 0x1C)[0]
    table_name = read_cstring(data, string_table_offset + table_name_offset)

    schema_cursor = 0x20
    columns = []
    for _ in range(field_count):
        raw_type = data[schema_cursor]
        name_offset = struct.unpack_from(">I", data, schema_cursor + 1)[0]
        schema_cursor += 5

        constant_value = None
        constant_offset = -1
        constant_size = 0
        storage = raw_type & 0xF0
        if storage in (0x30, 0x70):
            constant_value, constant_offset, constant_size = read_utf_value(
                data, raw_type, schema_cursor, string_table_offset, data_offset, base_offset
            )
            schema_cursor += constant_size

        columns.append(
            {
                "name": read_cstring(data, string_table_offset + name_offset),
                "raw_type": raw_type,
                "constant_value": constant_value,
                "constant_offset": constant_offset,
                "constant_size": constant_size,
            }
        )

    rows: list[list[UtfField]] = []
    for row_index in range(row_count):
        row_cursor = row_offset + row_size * row_index
        row_fields = []
        for column in columns:
            storage = column["raw_type"] & 0xF0
            if storage == 0x50:
                value, value_offset, value_size = read_utf_value(
                    data, column["raw_type"], row_cursor, string_table_offset, data_offset, base_offset
                )
                row_cursor += value_size
            elif storage == 0x10:
                value, value_offset, value_size = 0, -1, 0
            else:
                value = column["constant_value"]
                value_offset = column["constant_offset"]
                value_size = column["constant_size"]

            row_fields.append(UtfField(column["name"], column["raw_type"], value, value_offset, value_size))
        rows.append(row_fields)

    return UtfTable(table_name, rows)


def read_utf_value(
    data: bytes,
    raw_type: int,
    offset: int,
    string_table_offset: int,
    data_offset: int,
    base_offset: int,
) -> tuple[Any, int, int]:
    type_id = raw_type & 0x0F
    if type_id == 0x0A:
        string_offset = struct.unpack_from(">I", data, offset)[0]
        return read_cstring(data, string_table_offset + string_offset), base_offset + offset, 4
    if type_id == 0x0B:
        relative_offset = struct.unpack_from(">I", data, offset)[0]
        size = struct.unpack_from(">I", data, offset + 4)[0]
        value_offset = data_offset + relative_offset
        return data[value_offset:value_offset + size], base_offset + value_offset, 8
    if type_id == 0x08:
        return struct.unpack_from(">f", data, offset)[0], base_offset + offset, 4
    if type_id == 0x06:
        return struct.unpack_from(">Q", data, offset)[0], base_offset + offset, 8
    if type_id == 0x05:
        return struct.unpack_from(">i", data, offset)[0], base_offset + offset, 4
    if type_id == 0x04:
        return struct.unpack_from(">I", data, offset)[0], base_offset + offset, 4
    if type_id == 0x03:
        return struct.unpack_from(">h", data, offset)[0], base_offset + offset, 2
    if type_id == 0x02:
        return struct.unpack_from(">H", data, offset)[0], base_offset + offset, 2
    if type_id == 0x01:
        return struct.unpack_from(">b", data, offset)[0], base_offset + offset, 1
    if type_id == 0x00:
        return data[offset], base_offset + offset, 1
    return None, base_offset + offset, 0


def write_json(path: Path, payload: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def read_cri(path: Path) -> bytes:
    return decrypt_readable_copy(path.read_bytes(), path.name)


def field_by_name(row: list[UtfField], name: str) -> UtfField | None:
    for field in row:
        if field.name.lower() == name.lower():
            return field
    return None


def int_value(row: list[UtfField], name: str) -> int | None:
    field = field_by_name(row, name)
    if field is not None and isinstance(field.value, int):
        return field.value
    return None


def write_be_int(buffer: bytearray, offset: int, size: int, value: int) -> None:
    if size == 1:
        buffer[offset] = value & 0xFF
    elif size == 2:
        struct.pack_into(">H", buffer, offset, value)
    elif size == 4:
        struct.pack_into(">I", buffer, offset, value)
    elif size == 8:
        struct.pack_into(">Q", buffer, offset, value)
    else:
        raise ValueError(f"Unsupported integer patch size: {size}")


def patch_numeric_field(buffer: bytearray, row: list[UtfField], name: str, value: int) -> bool:
    field = field_by_name(row, name)
    if field is None or field.offset < 0 or field.size <= 0:
        return False
    write_be_int(buffer, field.offset, field.size, value)
    return True


def wav_sample_count(path: Path) -> int:
    with wave.open(str(path), "rb") as wav:
        return wav.getnframes()


def wav_info(path: Path) -> dict[str, Any]:
    with wave.open(str(path), "rb") as wav:
        sample_count = wav.getnframes()
        sample_rate = wav.getframerate()
        channels = wav.getnchannels()
        sample_width = wav.getsampwidth()
    loop = wav_smpl_loop(path)
    return {
        "path": str(path),
        "sample_count": sample_count,
        "sample_rate": sample_rate,
        "channels": channels,
        "sample_width_bytes": sample_width,
        "duration_seconds": sample_count / sample_rate if sample_rate > 0 else 0,
        "peaks": wav_peaks(path),
        "loop": None if loop is None else {
            "start": loop[0],
            "end": loop[1],
            "duration_samples": loop[1] - loop[0],
        },
    }


def audio_info(path: Path) -> dict[str, Any]:
    try:
        return wav_info(path)
    except (wave.Error, EOFError):
        pass

    ffmpeg_path, ffmpeg_source = resolve_ffmpeg(download=True)
    work_dir = data_root() / "work"
    work_dir.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="cri_audio_info_", dir=str(work_dir)) as temp_dir:
        prepared_wav = Path(temp_dir) / f"{path.stem}.analysis.wav"
        command = [
            ffmpeg_path,
            "-y",
            "-hide_banner",
            "-loglevel",
            "error",
            "-i",
            str(path),
            "-vn",
            "-acodec",
            "pcm_s16le",
            str(prepared_wav),
        ]
        subprocess.run(command, check=True)
        info = wav_info(prepared_wav)

    info["path"] = str(path)
    info["source_format"] = path.suffix.lower().lstrip(".") or "unknown"
    info["normalized_for_analysis"] = True
    info["ffmpeg"] = {"path": ffmpeg_path, "source": ffmpeg_source}
    info["loop"] = None
    return info


def wav_peaks(path: Path, buckets: int = 512) -> list[float]:
    with wave.open(str(path), "rb") as wav:
        frame_count = wav.getnframes()
        channels = max(1, wav.getnchannels())
        sample_width = wav.getsampwidth()
        if frame_count <= 0 or sample_width not in (1, 2, 3, 4):
            return []

        frames_per_bucket = max(1, frame_count // buckets)
        peaks: list[float] = []
        max_value = float((1 << (sample_width * 8 - 1)) - 1) if sample_width > 1 else 128.0
        remaining = frame_count
        while remaining > 0 and len(peaks) < buckets:
            take = min(frames_per_bucket, remaining)
            data = wav.readframes(take)
            remaining -= take
            peak = 0
            step = sample_width
            for offset in range(0, len(data), step):
                sample_bytes = data[offset:offset + step]
                if len(sample_bytes) != sample_width:
                    continue
                if sample_width == 1:
                    value = abs(sample_bytes[0] - 128)
                else:
                    value = abs(int.from_bytes(sample_bytes, "little", signed=True))
                if value > peak:
                    peak = value
            peaks.append(min(1.0, peak / max_value))

        return peaks


def wav_smpl_loop(path: Path) -> tuple[int, int] | None:
    data = path.read_bytes()
    if len(data) < 12 or data[:4] != b"RIFF" or data[8:12] != b"WAVE":
        return None

    cursor = 12
    while cursor + 8 <= len(data):
        chunk_id = data[cursor:cursor + 4]
        chunk_size = struct.unpack_from("<I", data, cursor + 4)[0]
        payload_offset = cursor + 8
        payload_end = payload_offset + chunk_size
        if payload_end > len(data):
            return None

        if chunk_id == b"smpl" and chunk_size >= 60:
            loop_count = struct.unpack_from("<I", data, payload_offset + 28)[0]
            if loop_count <= 0:
                return None

            first_loop = payload_offset + 36
            start = struct.unpack_from("<I", data, first_loop + 8)[0]
            end_inclusive = struct.unpack_from("<I", data, first_loop + 12)[0]
            return start, end_inclusive + 1

        cursor = payload_end + (chunk_size & 1)

    return None


def resolve_loop_points(args: argparse.Namespace, wav_path: Path) -> tuple[int | None, int | None, str]:
    if getattr(args, "no_loop", False):
        return None, None, "none"

    if args.loop_start is not None or args.loop_end is not None:
        if args.loop_start is None or args.loop_end is None:
            raise ValueError("Both --loop-start and --loop-end are required")
        sample_count = int(audio_info(wav_path).get("sample_count") or 0)
        if args.loop_start < 0 or args.loop_end <= args.loop_start or args.loop_end > sample_count:
            raise ValueError(f"Invalid loop range {args.loop_start}..{args.loop_end} for {sample_count} samples")
        return args.loop_start, args.loop_end, "explicit"

    if getattr(args, "loop_mode", "auto") == "auto":
        loop = wav_smpl_loop(wav_path)
        if loop is not None:
            sample_count = int(audio_info(wav_path).get("sample_count") or 0)
            if loop[0] < 0 or loop[1] <= loop[0] or loop[1] > sample_count:
                raise ValueError(f"Invalid WAV smpl loop range {loop[0]}..{loop[1]} for {sample_count} samples")
            return loop[0], loop[1], "wav-smpl"

    return None, None, "none"


def audio_root() -> Path:
    env = os.environ.get("L5_AUDIO_ROOT")
    if env:
        return Path(env).expanduser().resolve()
    return Path(__file__).resolve().parents[1]


def data_root() -> Path:
    env = os.environ.get("L5_AUDIO_DATA_ROOT")
    if env:
        return Path(env).expanduser().resolve()
    return audio_root()


def ffmpeg_platform_key() -> str:
    system = platform.system().lower()
    machine = platform.machine().lower()
    if system == "darwin":
        return "darwin-arm64" if machine in ("arm64", "aarch64") else "darwin-arm64"
    if system == "linux":
        return "linux-x64"
    if system == "windows":
        return "win-x64"
    raise RuntimeError(f"Unsupported platform for bundled FFmpeg: {platform.system()} {platform.machine()}")


def tool_available(path: str | Path, args: list[str]) -> bool:
    try:
        candidate = str(path)
        resolved = shutil.which(candidate) if not os.path.isabs(candidate) else None
        if resolved:
            candidate = resolved
        if os.path.isabs(candidate) and not Path(candidate).exists():
            return False
        result = subprocess.run([candidate, *args], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=3)
        return result.returncode == 0
    except Exception:
        return False


def vgmstream_available(path: str | Path) -> bool:
    try:
        candidate = str(path)
        resolved = shutil.which(candidate) if not os.path.isabs(candidate) else None
        if resolved:
            candidate = resolved
        if os.path.isabs(candidate) and not Path(candidate).exists():
            return False
        if Path(candidate).suffix.lower() == ".exe":
            required = ("avcodec-vgmstream-59.dll", "avformat-vgmstream-59.dll", "avutil-vgmstream-57.dll")
            if not all((Path(candidate).parent / name).exists() for name in required):
                return False
        result = subprocess.run([candidate, "-h"], capture_output=True, text=True, timeout=3)
        output = f"{result.stdout}\n{result.stderr}".lower()
        return "vgmstream" in output
    except Exception:
        return False


def plugin_executables(root: Path, names: tuple[str, ...]) -> list[Path]:
    if not root.exists():
        return []
    matches: list[Path] = []
    for name in names:
        direct = root / name
        if direct.is_file():
            matches.append(direct)
    try:
        for path in root.rglob("*"):
            if path.is_file() and path.name in names:
                matches.append(path)
    except OSError:
        pass
    return list(dict.fromkeys(matches))


def resolve_ffmpeg(download: bool = True) -> tuple[str, str]:
    env = os.environ.get("L5_AUDIO_FFMPEG_PATH")
    candidates: list[Path | str] = []
    if env:
        candidates.append(env)
    candidates.extend([
        "ffmpeg",
        "/opt/homebrew/bin/ffmpeg",
        "/opt/homebrew/opt/ffmpeg/bin/ffmpeg",
        "/usr/local/bin/ffmpeg",
        data_root() / "PlugIns" / ("ffmpeg.exe" if platform.system().lower() == "windows" else "ffmpeg"),
        data_root() / ".cache" / "dependencies" / ("ffmpeg.exe" if platform.system().lower() == "windows" else "ffmpeg"),
        audio_root() / "PlugIns" / ("ffmpeg.exe" if platform.system().lower() == "windows" else "ffmpeg"),
    ])
    candidates.extend(plugin_executables(data_root() / "PlugIns", ("ffmpeg", "ffmpeg.exe")))
    candidates.extend(plugin_executables(audio_root() / "PlugIns", ("ffmpeg", "ffmpeg.exe")))
    for candidate in candidates:
        if tool_available(candidate, ["-version"]):
            return str(candidate), "existing"

    if not download:
        raise FileNotFoundError("FFmpeg not found")

    key = ffmpeg_platform_key()
    asset, expected_sha = FFMPEG_ASSETS[key]
    cache_dir = data_root() / ".cache" / "dependencies"
    cache_dir.mkdir(parents=True, exist_ok=True)
    archive_path = cache_dir / asset
    executable = cache_dir / ("ffmpeg.exe" if key == "win-x64" else "ffmpeg")
    if not executable.exists() or not tool_available(executable, ["-version"]):
        download_verified(f"{FFMPEG_RELEASE_BASE}/{asset}", archive_path, expected_sha)
        with gzip.open(archive_path, "rb") as source, executable.open("wb") as output:
            shutil.copyfileobj(source, output)
        executable.chmod(0o755)
    return str(executable), "downloaded"


def resolve_vgmstream(explicit: str | None = None) -> tuple[str | None, str]:
    candidates: list[Path | str] = []
    if explicit:
        candidates.append(explicit)

    env = os.environ.get("L5_AUDIO_VGMSTREAM_PATH")
    if env:
        candidates.append(env)

    candidates.extend([
        "vgmstream-cli",
        "/opt/homebrew/bin/vgmstream-cli",
        "/opt/homebrew/opt/vgmstream/bin/vgmstream-cli",
        "/usr/local/bin/vgmstream-cli",
        data_root() / "PlugIns" / "Mac" / "vgmstream-cli",
        data_root() / "PlugIns" / "Linux" / "vgmstream-cli",
        data_root() / "PlugIns" / "Windows" / "vgmstream-cli.exe",
        audio_root() / "PlugIns" / "Mac" / "vgmstream-cli",
        audio_root() / "PlugIns" / "Linux" / "vgmstream-cli",
        audio_root() / "PlugIns" / "Windows" / "vgmstream-cli.exe",
        audio_root() / "PlugIns" / "vgmstream-cli",
        audio_root() / "PlugIns" / "vgmstream-cli.exe",
    ])
    candidates.extend(plugin_executables(data_root() / "PlugIns", ("vgmstream-cli", "vgmstream-cli.exe")))
    candidates.extend(plugin_executables(audio_root() / "PlugIns", ("vgmstream-cli", "vgmstream-cli.exe")))
    for candidate in candidates:
        if vgmstream_available(candidate):
            return str(candidate), "existing"

    return None, "missing"


def bundled_vgmstream_destination() -> Path:
    system = platform.system().lower()
    if system == "windows":
        return data_root() / "PlugIns" / "Windows" / "vgmstream-cli.exe"
    if system == "linux":
        return data_root() / "PlugIns" / "Linux" / "vgmstream-cli"
    return data_root() / "PlugIns" / "Mac" / "vgmstream-cli"


def copy_vgmstream_bundle(source_executable: Path, destination_executable: Path) -> None:
    destination_executable.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source_executable, destination_executable)
    if source_executable.suffix.lower() == ".exe":
        for dependency in source_executable.parent.glob("*.dll"):
            shutil.copy2(dependency, destination_executable.parent / dependency.name)
    else:
        destination_executable.chmod(destination_executable.stat().st_mode | 0o755)


def vgmstream_asset_name() -> str:
    system = platform.system().lower()
    if system == "windows":
        return "vgmstream-win64.zip"
    if system == "linux":
        return "vgmstream-linux-cli.tar.gz"
    if system == "darwin":
        return "vgmstream-mac-cli.tar.gz"
    raise RuntimeError(f"Unsupported platform for vgmstream: {platform.system()} {platform.machine()}")


def download_vgmstream() -> tuple[str | None, str]:
    asset_name = vgmstream_asset_name()
    cache_dir = data_root() / ".cache" / "dependencies" / "vgmstream"
    extract_dir = cache_dir / "extract"
    archive_path = cache_dir / asset_name
    cache_dir.mkdir(parents=True, exist_ok=True)
    extract_dir.mkdir(parents=True, exist_ok=True)

    with urllib.request.urlopen(VGMSTREAM_RELEASE_API, timeout=30) as response:
        release = json.load(response)
    assets = release.get("assets") or []
    asset = next((item for item in assets if item.get("name") == asset_name), None)
    if asset is None:
        return None, "download-asset-missing"

    download_url = asset.get("browser_download_url")
    if not download_url:
        return None, "download-url-missing"

    urllib.request.urlretrieve(download_url, archive_path)

    shutil.rmtree(extract_dir)
    extract_dir.mkdir(parents=True, exist_ok=True)
    if asset_name.endswith(".zip"):
        with zipfile.ZipFile(archive_path) as archive:
            archive.extractall(extract_dir)
    else:
        with tarfile.open(archive_path, "r:gz") as archive:
            archive.extractall(extract_dir)

    executable_name = "vgmstream-cli.exe" if platform.system().lower() == "windows" else "vgmstream-cli"
    executable = next((path for path in extract_dir.rglob(executable_name) if path.is_file()), None)
    if executable is None:
        return None, "download-executable-missing"

    destination = bundled_vgmstream_destination()
    copy_vgmstream_bundle(executable, destination)

    if vgmstream_available(destination):
        return str(destination), "downloaded"
    return None, "download-invalid"


def download_verified(url: str, destination: Path, expected_sha256: str) -> None:
    if destination.exists() and sha256_file(destination) == expected_sha256:
        return
    temporary = destination.with_suffix(destination.suffix + ".download")
    if temporary.exists():
        temporary.unlink()
    with urllib.request.urlopen(url, timeout=30) as response, temporary.open("wb") as output:
        shutil.copyfileobj(response, output)
    actual = sha256_file(temporary)
    if actual != expected_sha256:
        temporary.unlink(missing_ok=True)
        raise RuntimeError(f"Checksum mismatch for {url}: {actual}")
    temporary.replace(destination)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def helper_project_path(args: argparse.Namespace | None = None) -> Path:
    explicit = getattr(args, "hca_tool", None) if args is not None else None
    return Path(explicit) if explicit else Path(__file__).resolve().parent / "CriHcaTool" / "CriHcaTool.csproj"


def helper_executable_path() -> Path | None:
    name = "CriHcaTool.exe" if platform.system().lower() == "windows" else "CriHcaTool"
    candidates = [
        audio_root() / "tools" / "CriHcaToolRuntime" / name,
        Path(__file__).resolve().parent / "CriHcaToolRuntime" / name,
    ]
    for candidate in candidates:
        if candidate.exists():
            return candidate
    return None


def helper_command(args: argparse.Namespace | None, command_args: list[str]) -> list[str]:
    executable = helper_executable_path()
    if executable is not None:
        return [str(executable), *command_args]

    helper_project = helper_project_path(args)
    return ["dotnet", "run", "--project", str(helper_project), "--", *command_args]


def inspect_hca_with_helper(hca_path: Path, helper_project: Path | None = None) -> dict[str, Any]:
    command = [str(helper_executable_path()), "inspect", str(hca_path)] if helper_executable_path() is not None else [
        "dotnet", "run", "--project", str(helper_project or helper_project_path()), "--", "inspect", str(hca_path)
    ]
    result = subprocess.run(
        command,
        check=True,
        capture_output=True,
        text=True,
    )
    json_start = result.stdout.find("{")
    if json_start < 0:
        raise ValueError(f"HCA helper did not return JSON for {hca_path}: {result.stdout.strip()}")
    return json.loads(result.stdout[json_start:])


def select_awb_entry(archive: AwbArchive, entry_id: int | None, index: int | None) -> AwbEntry:
    for entry in archive.entries:
        if entry_id is not None and entry.id == entry_id:
            return entry
        if index is not None and entry.index == index:
            return entry
    raise ValueError(f"No AWB entry found for id={entry_id} index={index}")


def estimate_hca_bitrate(entry: AwbEntry, hca_metadata: dict[str, Any]) -> int | None:
    hca = hca_metadata.get("Hca") or {}
    sample_rate = int(hca.get("SampleRate") or 0)
    sample_count = int(hca.get("SampleCount") or 0)
    if sample_rate <= 0 or sample_count <= 0:
        return None
    return max(24000, round(entry.size * 8 * sample_rate / sample_count))


def normalize_audio_for_original(
    input_path: Path,
    output_path: Path,
    original_hca: dict[str, Any],
    ffmpeg_path: str,
) -> dict[str, Any]:
    hca = original_hca.get("Hca") or {}
    target_rate = int(hca.get("SampleRate") or 48000)
    target_channels = int(hca.get("ChannelCount") or 1)
    command = [
        ffmpeg_path,
        "-y",
        "-loglevel",
        "error",
        "-i",
        str(input_path),
        "-vn",
        "-acodec",
        "pcm_s16le",
        "-ar",
        str(target_rate),
        "-ac",
        str(target_channels),
        str(output_path),
    ]
    subprocess.run(command, check=True)
    info = wav_info(output_path)
    info["target_sample_rate"] = target_rate
    info["target_channels"] = target_channels
    return info


def waveform_duration_ms(row: list[UtfField]) -> int | None:
    samples = int_value(row, "NumSamples")
    rate = int_value(row, "SamplingRate")
    if samples is None or rate is None or rate <= 0:
        return None
    return round(samples * 1000 / rate)


def read_be_u16_items(data: bytes, count: int) -> list[int]:
    return [
        struct.unpack_from(">H", data, index * 2)[0]
        for index in range(min(count, len(data) // 2))
    ]


def command_synth_indices(data: bytes) -> list[int]:
    indices: list[int] = []
    patterns = (b"\x07\xd0\x04\x00\x02\x00", b"\x00\x4f\x05\x00\x01\x00")
    for pattern in patterns:
        start = 0
        while True:
            offset = data.find(pattern, start)
            if offset < 0:
                break
            value_offset = offset + len(pattern)
            if value_offset + 2 <= len(data):
                value = struct.unpack_from("<H", data, value_offset)[0]
                if value not in indices:
                    indices.append(value)
            start = offset + 1
    return indices


def resolve_synth_waveforms(synth_table: UtfTable | None, synth_index: int, seen: set[int] | None = None) -> list[int]:
    if synth_table is None or synth_index < 0 or synth_index >= len(synth_table.rows):
        return []
    if seen is None:
        seen = set()
    if synth_index in seen:
        return []
    seen.add(synth_index)

    field = field_by_name(synth_table.rows[synth_index], "ReferenceItems")
    data = field.value if field is not None and isinstance(field.value, bytes) else b""
    waveforms: list[int] = []
    for offset in range(0, len(data) - 3, 4):
        reference_type = struct.unpack_from(">H", data, offset)[0]
        reference_index = struct.unpack_from(">H", data, offset + 2)[0]
        if reference_type == 1:
            waveforms.append(reference_index)
        elif reference_type in (5, 7):
            waveforms.extend(resolve_synth_waveforms(synth_table, reference_index, seen))
    return waveforms


def resolve_cue_waveforms(
    cue_row: list[UtfField],
    waveform_table: UtfTable,
    sequence_table: UtfTable | None,
    synth_table: UtfTable | None,
    track_table: UtfTable | None = None,
    track_event_table: UtfTable | None = None,
    sequence_command_table: UtfTable | None = None,
) -> list[int]:
    reference_type = int_value(cue_row, "ReferenceType")
    reference_index = int_value(cue_row, "ReferenceIndex")
    if reference_type is None or reference_index is None or reference_index < 0:
        return []

    if reference_type == 1:
        return [reference_index] if reference_index < len(waveform_table.rows) else []

    if reference_type != 3 or sequence_table is None or reference_index >= len(sequence_table.rows):
        return []

    sequence_row = sequence_table.rows[reference_index]
    track_count = int_value(sequence_row, "NumTracks") or 0
    track_field = field_by_name(sequence_row, "TrackIndex")
    track_data = track_field.value if track_field is not None and isinstance(track_field.value, bytes) else b""
    waveforms: list[int] = []
    for track_index in read_be_u16_items(track_data, track_count):
        if track_table is not None and track_event_table is not None and 0 <= track_index < len(track_table.rows):
            event_index = int_value(track_table.rows[track_index], "EventIndex")
            if event_index is not None and 0 <= event_index < len(track_event_table.rows):
                command = field_by_name(track_event_table.rows[event_index], "Command")
                command_data = command.value if command is not None and isinstance(command.value, bytes) else b""
                for synth_index in command_synth_indices(command_data):
                    waveforms.extend(resolve_synth_waveforms(synth_table, synth_index))
                continue

        waveforms.extend(resolve_synth_waveforms(synth_table, track_index))

    if track_count == 0 and sequence_command_table is not None:
        command_index = int_value(sequence_row, "CommandIndex")
        if command_index is not None and 0 <= command_index < len(sequence_command_table.rows):
            command = field_by_name(sequence_command_table.rows[command_index], "Command")
            command_data = command.value if command is not None and isinstance(command.value, bytes) else b""
            for synth_index in command_synth_indices(command_data):
                waveforms.extend(resolve_synth_waveforms(synth_table, synth_index))
    return sorted({index for index in waveforms if 0 <= index < len(waveform_table.rows)})


def build_replace_checks(
    original_entry: AwbEntry,
    original_hca: dict[str, Any],
    input_info: dict[str, Any],
    prepared_info: dict[str, Any],
    hca_report: dict[str, Any],
    loop_start: int | None,
    loop_end: int | None,
    loop_source: str,
) -> list[dict[str, Any]]:
    checks: list[dict[str, Any]] = []
    original = original_hca.get("Hca") or {}
    original_rate = original.get("SampleRate")
    original_channels = original.get("ChannelCount")
    prepared_rate = prepared_info.get("sample_rate")
    prepared_channels = prepared_info.get("channels")
    hca_rate = (hca_report or {}).get("sampleRate") or (hca_report.get("hca") or {}).get("SampleRate") if isinstance(hca_report, dict) else None

    checks.append({
        "name": "sample_rate",
        "status": "fixed" if input_info.get("sample_rate") != prepared_rate else "ok",
        "original_hca": original_rate,
        "input": input_info.get("sample_rate"),
        "prepared": prepared_rate,
    })
    checks.append({
        "name": "channels",
        "status": "fixed" if input_info.get("channels") != prepared_channels else "ok",
        "original_hca": original_channels,
        "input": input_info.get("channels"),
        "prepared": prepared_channels,
    })
    checks.append({
        "name": "loop_range",
        "status": "ok" if loop_start is None or (loop_end is not None and 0 <= loop_start < loop_end <= int(prepared_info.get("sample_count") or 0)) else "warning",
        "loop_start": loop_start,
        "loop_end": loop_end,
        "prepared_samples": prepared_info.get("sample_count"),
    })
    if loop_source == "disabled-hca-loop-preserve-duration":
        checks.append({
            "name": "hca_loop",
            "status": "warning",
            "reason": "HCA loop metadata was not written because the current encoder trims the file to LoopEnd.",
        })
    original_samples = int(original.get("SampleCount") or 0)
    effective_samples = int(hca_report.get("hcaSampleCount") or prepared_info.get("sample_count") or 0) if isinstance(hca_report, dict) else int(prepared_info.get("sample_count") or 0)
    duration_ratio = effective_samples / original_samples if original_samples > 0 else None
    checks.append({
        "name": "duration",
        "status": "warning" if duration_ratio is not None and (duration_ratio < 0.75 or duration_ratio > 1.25) else "ok",
        "original_samples": original_samples,
        "new_samples": effective_samples,
        "ratio": None if duration_ratio is None else round(duration_ratio, 4),
    })
    original_looping = bool(original.get("Looping"))
    new_looping = loop_start is not None and loop_end is not None
    checks.append({
        "name": "loop_semantics",
        "status": "warning" if original_looping != new_looping else "ok",
        "original_looping": original_looping,
        "new_looping": new_looping,
        "original_loop_start": original.get("LoopStartSample"),
        "original_loop_end": original.get("LoopEndSample"),
        "new_loop_start": loop_start,
        "new_loop_end": loop_end,
    })
    new_size = hca_report.get("outputSize") if isinstance(hca_report, dict) else None
    checks.append({
        "name": "awb_entry_size",
        "status": "warning" if new_size is not None and original_entry.size > 0 and (new_size / original_entry.size > 1.5 or new_size / original_entry.size < 0.5) else "changed",
        "original_bytes": original_entry.size,
        "new_hca_bytes": new_size,
    })
    new_version = hca_report.get("hcaVersion") if isinstance(hca_report, dict) else None
    if new_version is not None:
        original_version = int(original_hca.get("Version") or 0)
        encoded_version = int(new_version or 0)
        status = "ok" if original_version == encoded_version else "warning"
        checks.append({
            "name": "hca_version",
            "status": status,
            "original_version": original_hca.get("Version"),
            "new_version": new_version,
        })
    checks.append({
        "name": "hca_encryption",
        "status": "warning" if int(original.get("EncryptionType") or 0) != 0 else "ok",
        "original_encryption_type": original.get("EncryptionType"),
        "new_encrypted": hca_report.get("encrypted") if isinstance(hca_report, dict) else None,
    })
    return checks


def cmd_inspect(args: argparse.Namespace) -> None:
    source = Path(args.source)
    data = read_cri(source)
    output = Path(args.output) if args.output else None

    if data.startswith(AWB_MAGIC):
        metadata = awb_metadata(parse_awb(data))
        if args.acb:
            clip_names = acb_awb_clip_name_lists(read_cri(Path(args.acb)))
            for entry in metadata["entries"]:
                names = clip_names.get(entry["id"], [])
                entry["name"] = names[0] if names else ""
                entry["cue_names"] = names
                entry["cue_count"] = len(names)
        if output:
            write_json(output / f"{source.name}.metadata.json", metadata)
        print(json.dumps(metadata, ensure_ascii=False, indent=2))
        return

    if data.startswith(ACB_MAGIC):
        table = parse_utf(data)
        metadata = table.to_json(include_rows=not args.summary)
        nested = table.nested_tables()
        metadata["nested_tables"] = {name: nested_table.to_json(include_rows=False) for name, nested_table in nested.items()}
        if output:
            write_json(output / f"{source.name}.metadata.json", metadata)
            tables_dir = output / "tables"
            for name, nested_table in nested.items():
                write_json(tables_dir / f"{safe_name(name)}.json", nested_table.to_json(include_rows=not args.summary))
        print(json.dumps(metadata, ensure_ascii=False, indent=2))
        return

    raise ValueError(f"Unsupported file magic in {source}")


def cmd_wav_info(args: argparse.Namespace) -> None:
    source = Path(args.source)
    print(json.dumps(audio_info(source), ensure_ascii=False, indent=2))


def cmd_clip_audio(args: argparse.Namespace) -> None:
    source = Path(args.source)
    target = Path(args.output)
    if args.sample_rate <= 0:
        raise ValueError("sample-rate must be positive")
    if args.start_sample < 0 or args.end_sample <= args.start_sample:
        raise ValueError("Invalid clip range")

    ffmpeg_path, ffmpeg_source = resolve_ffmpeg(download=True)
    target.parent.mkdir(parents=True, exist_ok=True)
    start_seconds = args.start_sample / args.sample_rate
    duration_seconds = (args.end_sample - args.start_sample) / args.sample_rate
    subprocess.run(
        [
            ffmpeg_path,
            "-y",
            "-hide_banner",
            "-loglevel",
            "error",
            "-ss",
            f"{start_seconds:.9f}",
            "-i",
            str(source),
            "-t",
            f"{duration_seconds:.9f}",
            "-vn",
            "-acodec",
            "pcm_s16le",
            str(target),
        ],
        check=True,
    )
    print(json.dumps(
        {
            "source": str(source),
            "output": str(target),
            "start_sample": args.start_sample,
            "end_sample": args.end_sample,
            "sample_rate": args.sample_rate,
            "ffmpeg": {"path": ffmpeg_path, "source": ffmpeg_source},
        },
        ensure_ascii=False,
        indent=2,
    ))


def cmd_ensure_plugins(args: argparse.Namespace) -> None:
    checks: list[dict[str, Any]] = []

    vgmstream_path, vgmstream_source = resolve_vgmstream(args.vgmstream)
    if vgmstream_path is None and not args.no_vgmstream_download:
        try:
            vgmstream_path, vgmstream_source = download_vgmstream()
        except Exception as exc:
            vgmstream_source = f"download-failed: {exc}"
    checks.append({
        "name": "vgmstream-cli",
        "available": vgmstream_path is not None,
        "path": vgmstream_path,
        "source": vgmstream_source,
    })

    try:
        ffmpeg_path, ffmpeg_source = resolve_ffmpeg(download=not args.no_ffmpeg_download)
        checks.append({
            "name": "ffmpeg",
            "available": True,
            "path": ffmpeg_path,
            "source": ffmpeg_source,
        })
    except Exception as exc:
        checks.append({
            "name": "ffmpeg",
            "available": False,
            "path": None,
            "source": "missing",
            "error": str(exc),
        })

    print(json.dumps({"checks": checks}, ensure_ascii=False, indent=2))


def cmd_unpack_awb(args: argparse.Namespace) -> None:
    source = Path(args.source)
    output = Path(args.output)
    archive = parse_awb(read_cri(source))
    clip_names: dict[int, list[str]] = {}
    if args.acb:
        clip_names = acb_awb_clip_name_lists(read_cri(Path(args.acb)))
    output.mkdir(parents=True, exist_ok=True)

    extracted = []
    for entry in archive.entries:
        names = clip_names.get(entry.id, [])
        primary_name = names[0] if names else ""
        name_suffix = f"_{safe_name(primary_name)}" if primary_name else ""
        filename = f"{entry.index:04d}_{entry.id:05d}{name_suffix}{entry.extension}"
        out_path = output / filename
        out_path.write_bytes(entry.data)
        extracted.append(
            {
                "index": entry.index,
                "id": entry.id,
                "name": primary_name,
                "cue_names": names,
                "cue_count": len(names),
                "file": filename,
                "size": entry.size,
                "extension": entry.extension,
                "sha1": hashlib.sha1(entry.data).hexdigest(),
            }
        )

    manifest = awb_metadata(archive)
    manifest["source"] = str(source)
    manifest["extracted"] = extracted
    write_json(output / "manifest.json", manifest)
    print(f"Extracted {len(extracted)} entries to {output}")


def cmd_preview_awb_entry(args: argparse.Namespace) -> None:
    source = Path(args.source)
    output = Path(args.output)
    archive = parse_awb(read_cri(source))
    entry = select_awb_entry(archive, args.id, args.index)
    acb_entry = acb_awb_entry_info(read_cri(Path(args.acb)), entry.id) if args.acb else None
    output.mkdir(parents=True, exist_ok=True)

    entry_path = output / f"{source.stem}_{entry.index:04d}_{entry.id:05d}{entry.extension}"
    entry_path.write_bytes(entry.data)
    wav_path = output / f"{source.stem}_{entry.index:04d}_{entry.id:05d}.wav"
    decoder = "copy"

    def decode_with_vgmstream() -> bool:
        nonlocal decoder
        vgmstream_path, _ = resolve_vgmstream(args.vgmstream)
        if vgmstream_path is None:
            return False
        wav_path.unlink(missing_ok=True)
        result = subprocess.run(
            [vgmstream_path, "-o", str(wav_path), str(entry_path)],
            capture_output=True,
            text=True,
        )
        if result.returncode == 0 and wav_path.exists() and wav_path.stat().st_size > 0:
            decoder = "vgmstream"
            return True
        return False

    if entry.extension == ".wav":
        shutil.copy2(entry_path, wav_path)
    elif entry.extension == ".hca":
        if not decode_with_vgmstream():
            subprocess.run(
                helper_command(args, [
                    "decode",
                    str(entry_path),
                    str(wav_path),
                ]),
                check=True,
                capture_output=True,
                text=True,
            )
            decoder = "cri-hca-tool"
    elif entry.extension == ".adx":
        if not decode_with_vgmstream():
            subprocess.run(
                helper_command(args, [
                    "decode-adx",
                    str(entry_path),
                    str(wav_path),
                ]),
                check=True,
                capture_output=True,
                text=True,
            )
            decoder = "cri-hca-tool-adx"
    else:
        raise ValueError(f"Preview is not supported for AWB entry type {entry.extension}")

    print(json.dumps(
        {
            "source": str(source),
            "entry": {
                "index": entry.index,
                "id": entry.id,
                "extension": entry.extension,
                "size": entry.size,
                "sha1": hashlib.sha1(entry.data).hexdigest(),
            },
            "extracted": str(entry_path),
            "wav": str(wav_path),
            "decoder": decoder,
            "acb_entry": acb_entry,
            "loop": acb_entry.get("loop") if acb_entry else None,
            "sample_count": acb_entry.get("sample_count") if acb_entry else None,
            "sample_rate": acb_entry.get("sample_rate") if acb_entry else None,
        },
        ensure_ascii=False,
        indent=2,
    ))


def parse_replacements(values: list[str], by_id: bool) -> dict[int, Path]:
    replacements: dict[int, Path] = {}
    for value in values:
        if "=" not in value:
            raise ValueError(f"Replacement must use KEY=PATH: {value}")
        raw_key, raw_path = value.split("=", 1)
        key = int(raw_key, 0)
        path = Path(raw_path)
        if not path.is_file():
            raise FileNotFoundError(path)
        replacements[key] = path
    return replacements


def cmd_replace_awb(args: argparse.Namespace) -> None:
    source = Path(args.source)
    target = Path(args.target)
    archive = parse_awb(read_cri(source))
    by_id = parse_replacements(args.replace_id or [], by_id=True)
    by_index = parse_replacements(args.replace_index or [], by_id=False)

    entries = []
    replaced = []
    for entry in archive.entries:
        replacement_path = by_id.get(entry.id) or by_index.get(entry.index)
        if replacement_path is None:
            entries.append(entry)
            continue

        replacement_data = replacement_path.read_bytes()
        entries.append(AwbEntry(entry.index, entry.id, entry.offset, replacement_data))
        replaced.append(
            {
                "index": entry.index,
                "id": entry.id,
                "old_size": entry.size,
                "new_size": len(replacement_data),
                "source": str(replacement_path),
                "extension": detect_extension(replacement_data),
            }
        )

    missing_ids = sorted(set(by_id) - {entry.id for entry in archive.entries})
    missing_indices = sorted(set(by_index) - {entry.index for entry in archive.entries})
    if missing_ids or missing_indices:
        raise ValueError(f"Missing replacements: ids={missing_ids}, indices={missing_indices}")

    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(build_awb(archive, entries))

    rebuilt = parse_awb(target.read_bytes())
    report = {
        "source": str(source),
        "target": str(target),
        "replaced": replaced,
        "result": awb_metadata(rebuilt),
    }
    write_json(target.with_suffix(target.suffix + ".replace-report.json"), report)
    print(f"Wrote {target} with {len(replaced)} replacement(s)")


def cmd_replace_awb_wav(args: argparse.Namespace) -> None:
    source = Path(args.source)
    target = Path(args.target)
    wav_path = Path(args.wav)
    if not wav_path.is_file():
        raise FileNotFoundError(wav_path)

    if (args.id is None) == (args.index is None):
        raise ValueError("Use exactly one of --id or --index")

    helper_project = helper_project_path(args)
    if not helper_project.exists():
        raise FileNotFoundError(f"HCA helper project not found: {helper_project}")

    target.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="cri_hca_", dir=str(target.parent)) as temp_dir:
        temp_root = Path(temp_dir)
        archive = parse_awb(read_cri(source))
        original_entry = select_awb_entry(archive, args.id, args.index)
        original_entry_path = temp_root / f"original_{original_entry.index:04d}_{original_entry.id:05d}{original_entry.extension}"
        original_entry_path.write_bytes(original_entry.data)
        original_hca = inspect_hca_with_helper(original_entry_path, helper_project) if original_entry.extension == ".hca" else {}
        original_hca_body = original_hca.get("Hca") or {}

        ffmpeg_path, ffmpeg_source = resolve_ffmpeg(download=not args.no_ffmpeg_download)
        prepared_wav = temp_root / f"{wav_path.stem}.prepared.wav"

        input_loop_start: int | None = None
        input_loop_end: int | None = None
        input_loop_source = "none"
        try:
            input_loop_start, input_loop_end, input_loop_source = resolve_loop_points(args, wav_path)
            input_info = audio_info(wav_path)
        except Exception:
            input_info = {"path": str(wav_path), "loop": None}
            if args.loop_start is not None or args.loop_end is not None:
                raise

        prepared_info = normalize_audio_for_original(wav_path, prepared_wav, original_hca, ffmpeg_path)
        input_rate = int(input_info.get("sample_rate") or prepared_info.get("sample_rate") or 0)
        prepared_rate = int(prepared_info.get("sample_rate") or input_rate or 0)
        loop_start, loop_end, loop_source = input_loop_start, input_loop_end, input_loop_source
        if loop_start is not None and loop_end is not None and input_rate > 0 and prepared_rate > 0 and input_rate != prepared_rate:
            loop_start = round(loop_start * prepared_rate / input_rate)
            loop_end = round(loop_end * prepared_rate / input_rate)
            loop_source = f"{loop_source}-scaled"
        if loop_start is not None and loop_end is not None:
            prepared_samples = int(prepared_info["sample_count"])
            loop_start = max(0, min(loop_start, prepared_samples - 1))
            loop_end = max(loop_start + 1, min(loop_end, prepared_samples))

        extension = ".hca" if args.codec == "HCA" else ".adx"
        hca_path = temp_root / f"{wav_path.stem}{extension}"
        bitrate = args.bitrate or estimate_hca_bitrate(original_entry, original_hca) if args.codec == "HCA" else None
        command = helper_command(args, [
            "encode" if args.codec == "HCA" else "encode-adx",
            str(prepared_wav),
            str(hca_path),
        ])
        if args.codec == "HCA":
            command.extend(["--quality", args.quality])
        if bitrate is not None:
            command.extend(["--bitrate", str(bitrate)])
        if args.key:
            command.extend(["--key", args.key])
        if args.codec == "HCA" and loop_start is not None and loop_end is not None:
            loop_start = None
            loop_end = None
            loop_source = "disabled-hca-loop-preserve-duration"
            command.append("--no-loop")
        elif loop_start is not None and loop_end is not None:
            command.extend(["--loop-start", str(loop_start), "--loop-end", str(loop_end)])
        else:
            command.append("--no-loop")
        encode_result = subprocess.run(command, check=True, capture_output=True, text=True)
        if encode_result.stdout:
            print(encode_result.stdout, end="" if encode_result.stdout.endswith("\n") else "\n")
        if encode_result.stderr:
            print(encode_result.stderr, end="" if encode_result.stderr.endswith("\n") else "\n")
        try:
            encode_report = json.loads(encode_result.stdout)
        except json.JSONDecodeError:
            encode_report = {"raw_stdout": encode_result.stdout}

        normalized_hca_ciph = False
        matched_hca_profile = False
        encoded_hca = inspect_hca_with_helper(hca_path, helper_project) if args.codec == "HCA" else {}
        if args.codec == "HCA" and args.match_original_hca_profile:
            matched_hca_profile = match_hca_header_profile(hca_path, original_hca)
            if matched_hca_profile:
                encoded_hca = inspect_hca_with_helper(hca_path, helper_project)
                if isinstance(encode_report, dict):
                    encode_report["matchedOriginalHcaProfile"] = True
        if args.codec == "HCA":
            original_encryption = int(original_hca_body.get("EncryptionType") or 0)
            encoded_encryption = int((encoded_hca.get("Hca") or {}).get("EncryptionType") or 0)
            if original_encryption == 0 and encoded_encryption == 1:
                normalized_hca_ciph = normalize_hca_type1_to_plain(hca_path)
                if normalized_hca_ciph:
                    encoded_hca = inspect_hca_with_helper(hca_path, helper_project)
                    if isinstance(encode_report, dict):
                        encode_report["normalizedHcaCiph"] = "type1-to-plain"
                        encode_report["hcaEncryptionType"] = (encoded_hca.get("Hca") or {}).get("EncryptionType")
            if isinstance(encode_report, dict):
                encode_report["hcaVersion"] = encoded_hca.get("Version")
                encode_report["hcaEncryptionType"] = (encoded_hca.get("Hca") or {}).get("EncryptionType")
                encode_report["minResolution"] = (encoded_hca.get("Hca") or {}).get("MinResolution")

        if args.keep_hca:
            keep_path = target.with_suffix(target.suffix + ".replacement.hca")
            shutil.copy2(hca_path, keep_path)

        replacement_arg = f"{args.id if args.id is not None else args.index}={hca_path}"
        replace_args = argparse.Namespace(
            source=str(source),
            target=str(target),
            replace_id=[replacement_arg] if args.id is not None else [],
            replace_index=[replacement_arg] if args.index is not None else [],
        )
        cmd_replace_awb(replace_args)

        report_path = target.with_suffix(target.suffix + ".wav-replace-report.json")
        hca_report = encode_report if isinstance(encode_report, dict) else {}
        write_json(report_path, {
            "source_awb": str(source),
            "target_awb": str(target),
            "input_audio": str(wav_path),
            "codec": args.codec,
            "encoder": "CriHcaTool",
            "prepared_by_ffmpeg": str(prepared_wav),
            "ffmpeg": {"path": ffmpeg_path, "source": ffmpeg_source},
            "selector": {"id": args.id, "index": args.index},
            "original_entry": {
                "index": original_entry.index,
                "id": original_entry.id,
                "size": original_entry.size,
                "extension": original_entry.extension,
                "hca": original_hca_body,
                "estimated_bitrate": estimate_hca_bitrate(original_entry, original_hca),
            },
            "input_audio_info": input_info,
            "prepared_wav_info": prepared_info,
            "loop_source": loop_source,
            "loop_start": loop_start,
            "loop_end": loop_end,
            "normalized_hca_ciph": normalized_hca_ciph,
            "matched_hca_profile": matched_hca_profile,
            "effective_sample_count": hca_report.get("hcaSampleCount") or prepared_info.get("sample_count"),
            "bitrate": bitrate,
            "checks": build_replace_checks(original_entry, original_hca, input_info, prepared_info, hca_report, loop_start, loop_end, loop_source),
            "hca": encode_report,
        })


def cmd_patch_acb_waveform(args: argparse.Namespace) -> None:
    source = Path(args.source)
    target = Path(args.target)
    if args.samples is None and not args.wav:
        raise ValueError("Use --samples or --wav")
    sample_count = args.samples if args.samples is not None else wav_sample_count(Path(args.wav))
    loop_start, loop_end, loop_source = resolve_loop_points(args, Path(args.wav)) if args.wav else (args.loop_start, args.loop_end, "explicit" if args.loop_start is not None else "none")
    if (loop_start is None) != (loop_end is None):
        raise ValueError("Both --loop-start and --loop-end are required")
    if loop_start is not None and (loop_start < 0 or loop_end <= loop_start or loop_end > sample_count):
        raise ValueError(f"Invalid loop range {loop_start}..{loop_end} for {sample_count} samples")
    effective_sample_count = sample_count
    if sample_count <= 0:
        raise ValueError("Sample count must be positive")
    if effective_sample_count <= 0:
        raise ValueError("Effective sample count must be positive")

    data = bytearray(read_cri(source))
    table = parse_utf_at(bytes(data), 0)
    nested = table.nested_tables()
    waveform_table = nested.get("WaveformTable")
    if waveform_table is None:
        raise ValueError("ACB has no WaveformTable")

    cue_table = nested.get("CueTable")
    sequence_table = nested.get("SequenceTable")
    synth_table = nested.get("SynthTable")
    track_table = nested.get("TrackTable")
    track_event_table = nested.get("TrackEventTable")
    sequence_command_table = nested.get("SeqCommandTable")
    extension_table = nested.get("WaveformExtensionDataTable")
    changed_waveforms = []
    old_durations_ms: dict[int, int] = {}
    for waveform_index, row in enumerate(waveform_table.rows):
        stream_id = int_value(row, "StreamAwbId")
        memory_id = int_value(row, "MemoryAwbId")
        streaming = int_value(row, "Streaming")
        matches_stream = not args.memory and streaming == 1 and stream_id == args.id
        matches_memory = args.memory and memory_id == args.id
        if not matches_stream and not matches_memory:
            continue

        num_samples = field_by_name(row, "NumSamples")
        if num_samples is None or num_samples.offset < 0 or num_samples.size <= 0:
            raise ValueError(f"Waveform row {waveform_index} has no patchable NumSamples field")

        old_duration = waveform_duration_ms(row)
        if old_duration is not None:
            old_durations_ms[waveform_index] = old_duration

        write_be_int(data, num_samples.offset, num_samples.size, effective_sample_count)
        patch_numeric_field(data, row, "EncodeType", args.encode_type)
        if args.channels is not None:
            patch_numeric_field(data, row, "NumChannels", args.channels)
        if args.sampling_rate is not None:
            patch_numeric_field(data, row, "SamplingRate", args.sampling_rate)
        if args.ch_config is not None:
            patch_numeric_field(data, row, "ChConfig", args.ch_config)
        loop_flag = field_by_name(row, "LoopFlag")
        if loop_flag is not None and loop_flag.offset >= 0 and loop_flag.size > 0:
            # CRI ACB stores 1 for no loop and 2 for an explicit loop range.
            write_be_int(data, loop_flag.offset, loop_flag.size, 2 if loop_start is not None and loop_end is not None else 1)
        extension_index = int_value(row, "ExtensionData")
        if (
            extension_table is not None and
            extension_index is not None and
            extension_index != 0xFFFF and
            0 <= extension_index < len(extension_table.rows) and
            loop_start is not None and
            loop_end is not None
        ):
            patch_numeric_field(data, extension_table.rows[extension_index], "LoopStart", loop_start)
            patch_numeric_field(data, extension_table.rows[extension_index], "LoopEnd", loop_end)
        changed_waveforms.append(waveform_index)

    if not changed_waveforms:
        archive_type = "MemoryAwbId" if args.memory else "StreamAwbId"
        raise ValueError(f"No waveform row found for {archive_type} {args.id}")

    changed_cues = []
    final_sampling_rate_by_waveform = {
        index: args.sampling_rate or int_value(waveform_table.rows[index], "SamplingRate") or 48000
        for index in changed_waveforms
    }
    new_durations_ms = {
        index: round(effective_sample_count * 1000 / max(1, final_sampling_rate_by_waveform[index]))
        for index in changed_waveforms
    }
    if args.patch_cue_lengths and cue_table is not None:
        changed_set = set(changed_waveforms)
        for cue_index, row in enumerate(cue_table.rows):
            waveform_indices = resolve_cue_waveforms(row, waveform_table, sequence_table, synth_table, track_table, track_event_table, sequence_command_table)
            if not changed_set.intersection(waveform_indices):
                continue

            length = field_by_name(row, "Length")
            if length is None or length.offset < 0 or length.size <= 0:
                continue

            old_length = int_value(row, "Length")
            if old_length is None or old_length == 0xFFFFFFFF:
                continue

            old_wave_durations = [old_durations_ms.get(index) for index in waveform_indices]
            old_wave_durations = [value for value in old_wave_durations if value is not None]
            new_wave_durations = [
                new_durations_ms.get(index) if index in changed_set else waveform_duration_ms(waveform_table.rows[index])
                for index in waveform_indices
            ]
            new_wave_durations = [value for value in new_wave_durations if value is not None]
            if not old_wave_durations or not new_wave_durations:
                continue

            old_max = max(old_wave_durations)
            new_max = max(new_wave_durations)
            single_changed = len(waveform_indices) == 1 and waveform_indices[0] in changed_set
            length_matches_waveform = abs(old_length - old_max) <= 2
            if single_changed or length_matches_waveform:
                write_be_int(data, length.offset, length.size, new_max)
                changed_cues.append(cue_index)

    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(data)
    report = {
        "source": str(source),
        "target": str(target),
        "awb_id": args.id,
        "archive": "memory" if args.memory else "stream",
        "wav_or_input_sample_count": sample_count,
        "effective_sample_count": effective_sample_count,
        "loop_source": loop_source,
        "loop_start": loop_start,
        "loop_end": loop_end,
        "encode_type": args.encode_type,
        "channels": args.channels,
        "sampling_rate": args.sampling_rate,
        "ch_config": args.ch_config,
        "changed_waveform_rows": changed_waveforms,
        "changed_cue_rows": changed_cues,
        "patch_cue_lengths": args.patch_cue_lengths,
    }
    write_json(target.with_suffix(target.suffix + ".patch-report.json"), report)
    print(json.dumps(report, ensure_ascii=False, indent=2))


def cmd_patch_acb_stream_awb(args: argparse.Namespace) -> None:
    source = Path(args.source)
    target = Path(args.target)
    awb_path = Path(args.awb)
    if not awb_path.is_file():
        raise FileNotFoundError(awb_path)

    awb_data = read_cri(awb_path)
    parse_awb(awb_data)
    awb_md5 = hashlib.md5(awb_data).digest()
    awb_name = args.name or awb_path.stem

    data = bytearray(read_cri(source))
    table = parse_utf_at(bytes(data), 0)
    nested = table.nested_tables()
    hash_table = nested.get("StreamAwbHash")
    header_table = nested.get("StreamAwbAfs2Header")

    changed_hash_rows = []
    if hash_table is not None:
        for row_index, row in enumerate(hash_table.rows):
            name = field_by_name(row, "Name")
            hash_field = field_by_name(row, "Hash")
            if hash_field is None or not isinstance(hash_field.value, bytes):
                continue
            if name is not None and isinstance(name.value, str) and name.value and name.value != awb_name:
                continue
            if len(hash_field.value) != len(awb_md5):
                raise ValueError(f"StreamAwbHash.Hash has unexpected size {len(hash_field.value)}")
            data[hash_field.offset:hash_field.offset + len(awb_md5)] = awb_md5
            changed_hash_rows.append(row_index)

    changed_header_rows = []
    if header_table is not None:
        for row_index, row in enumerate(header_table.rows):
            header = field_by_name(row, "Header")
            if header is None or not isinstance(header.value, bytes):
                continue
            if len(awb_data) < len(header.value):
                raise ValueError("AWB is shorter than stored StreamAwbAfs2Header.Header")
            replacement = awb_data[:len(header.value)]
            data[header.offset:header.offset + len(replacement)] = replacement
            changed_header_rows.append(row_index)

    if hash_table is not None and not changed_hash_rows:
        raise ValueError(f"No StreamAwbHash row matched AWB name '{awb_name}'")
    if header_table is not None and not changed_header_rows:
        raise ValueError("No StreamAwbAfs2Header.Header field was patched")

    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(data)
    report = {
        "source": str(source),
        "target": str(target),
        "awb": str(awb_path),
        "awb_name": awb_name,
        "awb_md5": awb_md5.hex(),
        "changed_hash_rows": changed_hash_rows,
        "changed_header_rows": changed_header_rows,
    }
    write_json(target.with_suffix(target.suffix + ".stream-awb-report.json"), report)
    print(json.dumps(report, ensure_ascii=False, indent=2))


def safe_name(value: str) -> str:
    cleaned = "".join("_" if char in '<>:"/\\|?*' else char for char in value)
    return cleaned or "unnamed"


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Inspect and conservatively repack CRI ACB/AWB audio files.")
    subparsers = parser.add_subparsers(dest="command", required=True)

    inspect_parser = subparsers.add_parser("inspect", help="Inspect ACB/AWB metadata.")
    inspect_parser.add_argument("source")
    inspect_parser.add_argument("--acb", help="Optional ACB used to resolve AWB clip/cue names.")
    inspect_parser.add_argument("--output")
    inspect_parser.add_argument("--summary", action="store_true", help="Omit full ACB row dumps from JSON.")
    inspect_parser.set_defaults(func=cmd_inspect)

    wav_info_parser = subparsers.add_parser("wav-info", help="Read WAV duration and first smpl loop.")
    wav_info_parser.add_argument("source")
    wav_info_parser.set_defaults(func=cmd_wav_info)

    clip_audio_parser = subparsers.add_parser("clip-audio", help="Write a WAV clip for playback.")
    clip_audio_parser.add_argument("source")
    clip_audio_parser.add_argument("--output", required=True)
    clip_audio_parser.add_argument("--start-sample", type=int, required=True)
    clip_audio_parser.add_argument("--end-sample", type=int, required=True)
    clip_audio_parser.add_argument("--sample-rate", type=int, required=True)
    clip_audio_parser.set_defaults(func=cmd_clip_audio)

    plugins_parser = subparsers.add_parser("ensure-plugins", help="Verify and integrate preview/transcode helper tools.")
    plugins_parser.add_argument("--vgmstream", help="Path to vgmstream-cli.")
    plugins_parser.add_argument("--no-vgmstream-download", action="store_true", help="Do not download vgmstream-cli if it is missing.")
    plugins_parser.add_argument("--no-ffmpeg-download", action="store_true", help="Do not download FFmpeg if it is missing.")
    plugins_parser.set_defaults(func=cmd_ensure_plugins)

    unpack_parser = subparsers.add_parser("unpack-awb", help="Extract AWB entries and write a manifest.")
    unpack_parser.add_argument("source")
    unpack_parser.add_argument("--output", required=True)
    unpack_parser.add_argument("--acb", help="Optional ACB used to name exported entries.")
    unpack_parser.set_defaults(func=cmd_unpack_awb)

    preview_parser = subparsers.add_parser("preview-awb-entry", help="Extract one AWB entry and decode it to WAV for preview.")
    preview_parser.add_argument("source")
    preview_parser.add_argument("--output", required=True)
    preview_parser.add_argument("--id", type=lambda value: int(value, 0))
    preview_parser.add_argument("--index", type=lambda value: int(value, 0))
    preview_parser.add_argument("--acb", help="Optional ACB used to restore original entry metadata in previews.")
    preview_parser.add_argument("--hca-tool", help="Path to CriHcaTool.csproj.")
    preview_parser.add_argument("--vgmstream", help="Path to vgmstream-cli.")
    preview_parser.set_defaults(func=cmd_preview_awb_entry)

    replace_parser = subparsers.add_parser("replace-awb", help="Write a new AWB with selected entries replaced.")
    replace_parser.add_argument("source")
    replace_parser.add_argument("target")
    replace_parser.add_argument("--replace-id", action="append", default=[], metavar="ID=PATH")
    replace_parser.add_argument("--replace-index", action="append", default=[], metavar="INDEX=PATH")
    replace_parser.set_defaults(func=cmd_replace_awb)

    replace_wav_parser = subparsers.add_parser(
        "replace-awb-wav",
        help="Encode a WAV to HCA and write a new AWB with one entry replaced.",
    )
    replace_wav_parser.add_argument("source")
    replace_wav_parser.add_argument("target")
    replace_wav_parser.add_argument("wav")
    replace_wav_parser.add_argument("--id", type=lambda value: int(value, 0))
    replace_wav_parser.add_argument("--index", type=lambda value: int(value, 0))
    replace_wav_parser.add_argument("--quality", default="High", choices=["Highest", "High", "Middle", "Low", "Lowest"])
    replace_wav_parser.add_argument("--codec", default="HCA", choices=["HCA", "ADX"], help="Output codec. ADX is compatible with the bundled open encoder.")
    replace_wav_parser.add_argument("--bitrate", type=int)
    replace_wav_parser.add_argument("--key", help="Optional HCA encryption key, decimal or 0x-prefixed.")
    replace_wav_parser.add_argument("--loop-mode", choices=["auto", "none"], default="auto", help="Auto reads the first WAV smpl loop when present.")
    replace_wav_parser.add_argument("--loop-start", type=int, help="Loop start sample, inclusive.")
    replace_wav_parser.add_argument("--loop-end", type=int, help="Loop end sample, exclusive.")
    replace_wav_parser.add_argument("--no-loop", action="store_true", help="Force no loop even if WAV has smpl metadata.")
    replace_wav_parser.add_argument("--no-ffmpeg-download", action="store_true", help="Fail instead of downloading bundled FFmpeg when no local FFmpeg is found.")
    replace_wav_parser.add_argument("--hca-tool", help="Path to CriHcaTool.csproj.")
    replace_wav_parser.add_argument("--keep-hca", action="store_true", help="Keep the encoded HCA next to the target AWB.")
    replace_wav_parser.add_argument("--match-original-hca-profile", action="store_true", help="Patch generated HCA version/min-resolution to match the replaced entry. Off by default for compatibility.")
    replace_wav_parser.set_defaults(func=cmd_replace_awb_wav)

    patch_acb_parser = subparsers.add_parser(
        "patch-acb-waveform",
        help="Patch WaveformTable.NumSamples for a stream or memory AWB id.",
    )
    patch_acb_parser.add_argument("source")
    patch_acb_parser.add_argument("target")
    patch_acb_parser.add_argument("--id", required=True, type=lambda value: int(value, 0))
    patch_acb_parser.add_argument("--samples", type=int)
    patch_acb_parser.add_argument("--wav", help="Read sample count from this WAV when --samples is omitted.")
    patch_acb_parser.add_argument("--loop-mode", choices=["auto", "none"], default="auto", help="Auto reads the first WAV smpl loop when present.")
    patch_acb_parser.add_argument("--loop-start", type=int, help="Loop start sample, inclusive.")
    patch_acb_parser.add_argument("--loop-end", type=int, help="Loop end sample, exclusive.")
    patch_acb_parser.add_argument("--no-loop", action="store_true", help="Patch LoopFlag to 1 (the CRI no-loop value).")
    patch_acb_parser.add_argument("--encode-type", type=int, default=2, help="Waveform EncodeType. 2 is HCA.")
    patch_acb_parser.add_argument("--channels", type=int, help="Patch WaveformTable.NumChannels.")
    patch_acb_parser.add_argument("--sampling-rate", type=int, help="Patch WaveformTable.SamplingRate.")
    patch_acb_parser.add_argument("--ch-config", type=int, help="Patch WaveformTable.ChConfig.")
    patch_acb_parser.add_argument("--memory", action="store_true", help="Match MemoryAwbId instead of StreamAwbId.")
    patch_acb_parser.add_argument("--patch-cue-lengths", action="store_true", help="Also update derived CueTable.Length values. Disabled by default for compatibility.")
    patch_acb_parser.set_defaults(func=cmd_patch_acb_waveform)

    patch_stream_awb_parser = subparsers.add_parser(
        "patch-acb-stream-awb",
        help="Patch ACB StreamAwbHash and StreamAwbAfs2Header from an AWB file.",
    )
    patch_stream_awb_parser.add_argument("source")
    patch_stream_awb_parser.add_argument("target")
    patch_stream_awb_parser.add_argument("--awb", required=True)
    patch_stream_awb_parser.add_argument("--name", help="StreamAwbHash.Name to patch. Defaults to AWB stem.")
    patch_stream_awb_parser.set_defaults(func=cmd_patch_acb_stream_awb)

    return parser


def main() -> None:
    parser = build_parser()
    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
