# CRI Audio Notes

## AWB / AFS2

Firma: `AFS2`.

Campos usados por la lógica de inspección:

- `0x05`: tamaño de cada offset (`2`, `4` u `8` bytes).
- `0x06`: tamaño de cada id (`2` o `4` bytes).
- `0x08`: número de archivos, little-endian `int32`.
- `0x0C`: alineación, little-endian `uint16`.
- `0x0E`: subkey AWB, little-endian `uint16`.
- `0x10`: tabla de IDs.
- después: tabla de offsets con `file_count + 1` entradas.

Para cada entrada:

- inicio real = `align(offsets[i], alignment)`.
- final = `offsets[i + 1]`.
- extensión inferida por firma: `HCA\0`, HCA cifrado `C8 C3 C1 00`, `RIFF`, o binario.

Al reempaquetar AWB se preservan:

- orden de entradas,
- IDs,
- alineación,
- subkey,
- tamaño de IDs cuando es posible,
- tamaño de offsets cuando es posible.

## ACB / @UTF

Firma: `@UTF`.

El parser de referencia lee cabecera en big-endian:

- `0x0A`: offset de filas + 8.
- `0x0C`: offset de strings + 8.
- `0x10`: offset de datos + 8.
- `0x14`: offset del nombre de tabla.
- `0x18`: número de columnas.
- `0x1A`: tamaño de fila.
- `0x1C`: número de filas.

Tablas relevantes observadas:

- `CueNameTable`: nombres de cues y `CueIndex`.
- `CueTable`: tipo e índice de referencia.
- `WaveformTable`: `Streaming`, `StreamAwbId`, `MemoryAwbId`, `EncodeType`, `NumChannels`, `SamplingRate`, `NumSamples`, `LoopFlag`, `ExtensionData`.
- `WaveformExtensionDataTable`: loop points explícitos (`LoopStart`, `LoopEnd`) para waveforms que tienen `ExtensionData != 0xFFFF`.
- `SequenceTable`: secuencias con tracks.
- `SynthTable`: referencias indirectas a waveforms.
- `StreamAwbHash`: MD5 del AWB externo cuando existe.

## Estado de modificación

La modificación segura inicial es reemplazar payloads dentro de AWB. Esto sirve cuando:

- se conserva el mismo ID de AWB,
- el formato insertado es compatible con el motor,
- y el ACB no necesita ajustes semánticos.

Riesgos pendientes para ACB:

- `NumSamples` puede quedar desactualizado si cambia la duración.
- `CueTable.Length` usa milisegundos y puede quedar incoherente si cambia la duración.
- `LoopFlag` y `WaveformExtensionDataTable` pueden quedar incoherentes si se añade o elimina loop.
- `EncodeType`, `NumChannels`, `SamplingRate` o `ChConfig` pueden bloquear o degradar reproducción si no coinciden con el HCA final.
- `StreamAwbHash` puede no coincidir si el juego valida MD5.
- `StreamAwbAfs2Header` puede no coincidir si el AWB cambia su header.
- cues compuestos pueden depender de secuencias y waveforms indirectos.
- HCA cifrado puede requerir key/subkey compatible.

## WAV -> HCA

`AUDIO/tools/CriHcaTool` usa `VGAudio 2.2.1`:

- `WaveReader` para leer WAV.
- `HcaWriter` + `HcaConfiguration` para escribir HCA.
- calidad por defecto: `High`.
- sin cifrado por defecto.
- loops mediante `IAudioFormat.WithLoop(true, start, end)`.

En `c11070100.awb`, los HCA inspeccionados usan `EncryptionType = 0`, así que el camino inicial sin key coincide con ese banco.

VGAudio escribe HCA `0x0200`, mientras que los bancos de Victory Road inspeccionados usan HCA `0x0300`. Se construyó `AUDIO/research/cri_android_decoder_probe.c` para ejecutar `libcri_ware_unity.so` de otro título de Level-5, que contiene `HCA Decoder (Float) Ver.3.10.00`, la misma versión que Victory Road. El decoder consumió y decodificó completamente tanto HCA 3 original como HCA 2 de VGAudio. La diferencia de versión no explica por sí sola el silencio observado.

Antes de llamar a `CriHcaTool`, `replace-awb-wav` prepara el input con FFmpeg:

- acepta audio que FFmpeg pueda leer,
- produce WAV PCM 16-bit temporal,
- fuerza sample rate del HCA original,
- fuerza número de canales del HCA original,
- estima bitrate desde el HCA original,
- conserva o escala loop points si el input era WAV con `smpl` o si el usuario los dio manualmente.

FFmpeg se resuelve en este orden aproximado:

- `L5_AUDIO_FFMPEG_PATH`,
- `PATH` / Homebrew,
- `AUDIO/.cache/dependencies`,
- descarga verificada desde `eugeneware/ffmpeg-static`.

Loop points:

- `cri_audio_tool.py` puede leer el primer loop del chunk WAV `smpl`.
- también acepta `--loop-start` y `--loop-end` explícitos.
- `loop_end` se trata como sample exclusivo.
- en pruebas con `VGAudio`, el HCA looped usa `loop_end` como `Hca.SampleCount`; por eso el parche ACB usa esa duración efectiva para `NumSamples`.

## Parche ACB actual

`patch-acb-waveform` modifica enteros existentes dentro del ACB sin reconstruir tablas:

- localiza `WaveformTable`,
- busca fila por `StreamAwbId` o `MemoryAwbId`,
- parchea `NumSamples`,
- parchea `EncodeType`,
- parchea `NumChannels`,
- parchea `SamplingRate`,
- parchea `ChConfig`,
- parchea `LoopFlag`,
- parchea `WaveformExtensionDataTable.LoopStart/LoopEnd` cuando existe fila de extensión,
- parchea `CueTable.Length` para cues que resuelven de forma segura al waveform.

No cambia el tamaño del ACB.

La resolución de cues cubre:

- cues directos a waveform,
- `SequenceTable -> TrackIndex -> SynthTable.ReferenceItems`,
- referencias anidadas de `SynthTable` observadas como tipos `5` y `7`.

`CueTable.Length` usa milisegundos, pero se conserva por defecto porque puede formar parte del timing autoral de secuencias complejas. Sólo se actualiza al solicitar `--patch-cue-lengths`; en ese modo se cambia cuando el cue resuelve exactamente al waveform modificado o cuando la duración previa coincide con la duración máxima calculada de sus waveforms.

`patch-acb-stream-awb` actualiza metadatos de emparejamiento entre ACB y AWB:

- `StreamAwbHash.Hash`: MD5 del AWB final.
- `StreamAwbAfs2Header.Header`: prefijo/header AFS2 del AWB final.

En `c11070100.acb`, el MD5 original encontrado en `StreamAwbHash.Hash` coincide exactamente con el MD5 de `c11070100.awb`. El header almacenado en `StreamAwbAfs2Header.Header` coincide byte a byte con el prefijo del AWB.

## Contraste con ejecutable

El proyecto Ghidra lista `nie.exe`, `nie_602.exe` y librerías auxiliares. En la instalación Steam disponible, `nie.exe` contiene CRI AtomEx/Atom embebido:

- `CRI AtomEx/PCx64 Ver.2.29.4 Build:Jul  9 2025`
- `CRI Atom/PCx64 Ver.2.29.332`
- `HCA Codec Plugin Ver.1.04.02`

Strings relevantes:

- `Failed to load AWB file.`
- `Input audio data buffer is invalid. ACB and AWB might not be same version.`
- `AWB type mismatch.`
- `Invalid AWB location. First AWB is skipped.`

Conclusión: el motor probablemente delega la carga en CRI AtomEx, así que el AWB modificado debe seguir coincidiendo con los metadatos CRI que el ACB trae embebidos. Actualizar solo payloads y `NumSamples` es insuficiente para máxima compatibilidad.

## Observaciones en anime_stream

`anime_stream.awb` es un caso útil porque es grande y variado:

- AWB externo de unos 624 MB.
- 111 entradas HCA.
- 187 cues.
- 111 waveforms.
- 270 filas de synth.
- 187 secuencias.

Distribución observada en `WaveformTable`:

- `EncodeType`: todos `2`, compatible con HCA.
- `Streaming`: todos `1`, todos van por AWB externo.
- `NumChannels`: todos `2`.
- `SamplingRate`: todos `48000`.
- `LoopFlag`: mayoría `1`, algunos `2`.
- `ExtensionData`: la mayoría `0xFFFF`; solo tres waveforms apuntan a `WaveformExtensionDataTable`.

Este banco confirmó que no basta con cues directos. Hay cues que resuelven cero, uno o varios waveforms a través de secuencias y synths, y varias duraciones de `CueTable.Length` no son una simple conversión de `NumSamples`. Por eso el parche actual es conservador: corrige lo derivable y deja intacto lo que parece timing autoral del banco.

La semántica observada de `LoopFlag` es `1` para reproducción sin loop y `2` para un loop explícito. No se ha encontrado ningún waveform válido con valor `0`; el parche usa esos mismos valores.

## Checks automáticos

El reporte `*.wav-replace-report.json` compara y/o corrige:

- sample rate,
- canales,
- loop range,
- bitrate aproximado,
- tamaño del payload HCA,
- duración efectiva codificada,
- campos de formato ACB parcheables,
- `EncryptionType` del HCA original.

Si `EncryptionType` original no es `0`, el reporte marca warning. En ese caso todavía hay que confirmar key/cifrado antes de asumir compatibilidad total.
