# AUDIO

Zona de trabajo para investigar y modificar audio CRI de Victory Road.

Los bancos principales son:

- `.acb`: metadatos CRI en tablas `@UTF`. Normalmente contiene nombres de cues, `CueTable`, `WaveformTable`, `SequenceTable`, `SynthTable`, hashes y a veces AWB embebidos.
- `.awb`: contenedor AFS2 con los datos de audio, normalmente HCA/ADX/WAV. El ACB enlaza cues con IDs dentro del AWB.

Base técnica tomada de `L5Decompiler`, especialmente:

- `/Users/bobi/Documents/GitHub/L5Decompiler/L5Extractor.Core/Utilities/CriNativeExtractor.cs`
- `/Users/bobi/Documents/GitHub/L5Decompiler/L5Extractor.Core/Utilities/CriReadableDecryptor.cs`

## Flujo seguro

1. Inspeccionar ACB/AWB y generar JSON de metadatos.
2. Extraer AWB a entradas individuales con manifiesto.
3. Reemplazar entradas concretas en una copia nueva del AWB.
4. Validar que IDs, conteo, alineación y subkey se mantienen.
5. Solo después investigar si el ACB necesita cambios de duración, muestras, hashes o cues.

## Herramienta inicial

```bash
python3 AUDIO/tools/cri_audio_tool.py inspect "/ruta/banco.acb" --output AUDIO/work/banco_inspect
python3 AUDIO/tools/cri_audio_tool.py unpack-awb "/ruta/banco.awb" --output AUDIO/work/banco_awb
python3 AUDIO/tools/cri_audio_tool.py replace-awb "/ruta/banco.awb" AUDIO/work/banco_mod.awb --replace-id 12=/ruta/nuevo.hca
```

La herramienta crea copias legibles si el archivo usa el cifrado simple por nombre que ya maneja `L5Decompiler`.

## Sustituir una entrada usando audio

El flujo recomendado para una entrada de audio externo es:

```bash
python3 AUDIO/tools/cri_audio_tool.py replace-awb-wav \
  "/ruta/original.awb" \
  AUDIO/work/banco_mod.awb \
  "/ruta/nuevo.wav" \
  --id 12
```

Aunque el comando conserve el nombre `replace-awb-wav`, el backend puede preparar otros formatos soportados por FFmpeg (`flac`, `ogg`, `mp3`, `m4a`, `aiff`, etc.). Antes de codificar a HCA:

- inspecciona la entrada HCA original,
- estima su bitrate,
- busca o descarga FFmpeg,
- convierte el audio a PCM 16-bit,
- ajusta sample rate al original,
- ajusta canales al original,
- escala loop points si cambia el sample rate,
- codifica a HCA usando un bitrate cercano al original cuando el encoder es compatible.

Loop points:

- por defecto, si el WAV tiene chunk `smpl`, se usa el primer loop encontrado;
- puedes forzar puntos con `--loop-start N --loop-end N`;
- puedes desactivar loops con `--no-loop` o `--loop-mode none`;
- `--loop-end` es exclusivo.

Si el WAV dura distinto al audio original, parchea también el ACB asociado:

```bash
python3 AUDIO/tools/cri_audio_tool.py patch-acb-waveform \
  "/ruta/original.acb" \
  AUDIO/work/banco_mod.acb \
  --id 12 \
  --wav "/ruta/nuevo.wav"
```

Si el WAV tiene loop, `patch-acb-waveform` marca `LoopFlag` y usa `loop_end` como duración efectiva en `NumSamples`, que coincide con la longitud base que escribe el encoder HCA.

`replace-awb-wav` usa `AUDIO/tools/CriHcaTool`, un helper mínimo en C# con `VGAudio`, para convertir WAV a HCA antes de reempaquetar el AWB. También puede usar opcionalmente un encoder CRI externo:

```bash
python3 AUDIO/tools/cri_audio_tool.py replace-awb-wav \
  "/ruta/original.awb" AUDIO/work/banco_mod.awb "/ruta/nuevo.wav" --id 12 \
  --cri-hca-encoder "/ruta/a/CriAtomEncoder"
```

El ejecutable se invoca con HCA, sample rate, calidad y loop ya normalizados.

Por ahora se actualiza:

- payload HCA dentro del AWB,
- offsets/tamaños/alineación del AWB,
- `WaveformTable.NumSamples` en el ACB,
- `WaveformTable.EncodeType`, `NumChannels`, `SamplingRate` y `ChConfig` cuando el reemplazo lo permite inferir,
- `WaveformTable.LoopFlag` en el ACB (`1` sin loop, `2` con loop),
- `WaveformExtensionDataTable.LoopStart/LoopEnd` si el banco ya tiene fila de extensión para ese waveform,
- `CueTable.Length` en milisegundos cuando el cue resuelve de forma segura al waveform cambiado,
- `StreamAwbHash.Hash` con el MD5 del AWB final,
- `StreamAwbAfs2Header.Header` con el header AFS2 del AWB final.

Cada reemplazo genera `*.wav-replace-report.json` con checks de:

- sample rate,
- canales,
- loop range,
- tamaño de entrada AWB,
- cifrado HCA original.

Pendiente para bancos que lo requieran:

- cues con timing de secuencia muy específico pueden mantener `CueTable.Length` si no hay una equivalencia segura con la duración del waveform,
- añadir filas nuevas a `WaveformExtensionDataTable` si un waveform sin extensión necesita loop points ACB explícitos,
- replicar configuraciones especiales de cifrado HCA si aparece `EncryptionType` distinto de `0`.

## UI Avalonia

Hay un frontend en:

```bash
AUDIO/AudioTool.Gui
```

Para ejecutarlo:

```bash
dotnet run --project AUDIO/AudioTool.Gui/AudioTool.Gui.csproj
```

La UI llama al backend Python `AUDIO/tools/cri_audio_tool.py` para:

- leer entradas AWB,
- leer duración y loop del WAV cuando el formato lo expone directamente,
- preparar audio con FFmpeg,
- generar AWB modificado,
- parchear ACB.
- actualizar hash/header del AWB dentro del ACB.

La región de loop se controla con un doble slider:

- el tirador izquierdo marca `loop_start`,
- el tirador derecho marca `loop_end`,
- `loop_end` se trata como sample exclusivo,
- el modo `Auto WAV smpl` usa el primer loop embebido en el WAV,
- el modo `Manual` fuerza los valores del slider,
- el modo `Sin loop` desactiva loop y parchea `LoopFlag = 1`.

FFmpeg:

- primero se busca en `PATH`, Homebrew y caches locales;
- si existe la cache de `L5Decompiler`, se reutiliza;
- si no aparece, se descarga desde el mismo origen usado por `L5Decompiler` (`eugeneware/ffmpeg-static`) con SHA-256 verificado;
- la descarga queda en `AUDIO/.cache/dependencies`.

## Validación frente al motor

El ejecutable cargado en el proyecto Ghidra corresponde a `nie.exe` de la instalación Steam. Sus strings indican que usa CRI AtomEx/Atom nativo (`CRI AtomEx/PCx64 Ver.2.29.4`) y contiene errores internos como:

- `Cannot open ACB file`
- `Failed to load AWB file`
- `Input audio data buffer is invalid. ACB and AWB might not be same version.`
- `AWB type mismatch`
- `Invalid AWB location`

En los ACB reales, `StreamAwbHash.Hash` contiene el MD5 exacto del AWB externo y `StreamAwbAfs2Header.Header` contiene el prefijo/header AFS2 del AWB. Por eso el flujo actual parchea ambos después de modificar el AWB.

El HCA de `c01000010` es versión 3 (`0x0300`), mientras que VGAudio 2.2.1 escribe versión 2 (`0x0200`). El probe Android ejecutado contra `HCA Decoder (Float) Ver.3.10.00`, la misma versión incluida por Victory Road, consume y decodifica completamente ambos bitstreams. Por tanto, la versión HCA no es por sí sola el bloqueo de reproducción.

## Caso grande: anime_stream

`anime_stream.awb` pesa alrededor de 624 MB y contiene 111 entradas HCA. Es un buen banco de estrés porque `anime_stream.acb` no enlaza todos los cues de forma directa: muchos pasan por `SequenceTable`, `TrackIndex` y referencias anidadas de `SynthTable`.

Checks añadidos a partir de ese banco:

- resolución de cues indirectos `SequenceTable -> SynthTable -> WaveformTable`,
- soporte de referencias sintéticas anidadas observadas como tipos `5` y `7`,
- `CueTable.Length` tratado como milisegundos, no muestras,
- actualización conservadora de cues compuestos para no pisar duraciones editadas a mano por CRI,
- parcheo de loop points en `WaveformExtensionDataTable` cuando el ACB ya contiene esa fila.

En bancos grandes, el reemplazo reescribe el AWB completo. Es normal que tarde más y que el archivo final cambie de tamaño si el HCA generado usa otra duración o bitrate.
