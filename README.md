# PortableStreamer — плановый стример для CDN

Программа для Windows, которая в назначенное время сама поднимает трансляцию:

```
заставка (intro) → основной видеоролик → финальная заставка (outro) → CDN
```

Поставил время, нажал ПУСК — эфир начнётся сам, даже если окно свёрнуто.

## Возможности

- **Эфир по расписанию**: заставка, основной файл, финал, точная длительность.
- **Тестовый цикл**: непрерывный тестовый поток на 30/60/90 минут без подготовки файлов.
  Источники: повтор видео, картинка + тон 1 кГц, картинка + свой MP3, таблица SMPTE.
- **Часы на экране** (`clockoverlay`) — для замера задержки CDN.
- **Превью эфира** прямо в окне (отдельный процесс, не влияет на трансляцию).
- **Плашка состояния CDN**: подключение, публикация, отказ, таймаут.
- **Проверки до старта**: адрес и ключ CDN, занятость порта 1935, энкодер.
- **Логи с историей**: ID машины в имени, ключ потока маскируется.
- **Один экземпляр** на компьютер, уборка «осиротевших» процессов.

Два исполняемых файла из одного кода:

| Файл | Назначение |
|---|---|
| `PortableStreamer.exe` | публикация по RTMP |
| `PortableStreamerSRT.exe` | публикация по SRT (сборка с флагом `SRT_BUILD`) |

## Требования

- Windows 10 / 11, 64-бит
- .NET 10 SDK — только для сборки (готовый релиз самодостаточен)
- Свободные порты 1935 и 8554 (слушаются только на 127.0.0.1)

## Как это работает

```
GStreamer (gst-launch) --RTMP--> MediaMTX --RTMP/SRT--> CDN
```

- **GStreamer** собирает единый конвейер: `concat` → энкодер → `flvmux` → `rtmpsink`,
  ключевой кадр раз в секунду, аппаратный энкодер с откатом на x264.
- **MediaMTX** принимает поток локально и держит публикацию на CDN
  (переподключается при обрывах, умеет отдавать наружу RTMP или SRT).
- **Превью** — отдельный процесс GStreamer, читает поток из MediaMTX по RTSP
  и отдаёт JPEG по TCP. Проблема с превью физически не может остановить эфир.

Подробности архитектуры и все проверенные решения — в [PROJECT_SPEC.md](PROJECT_SPEC.md).

## Сборка

```bat
dotnet publish PortableStreamer.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -o publish

dotnet publish SrtBuild\PortableStreamerSrt.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -o publish
```

Рядом с exe должны лежать `tools\` (GStreamer + MediaMTX) — в репозиторий они не входят
из-за размера и скачиваются отдельно.

## Структура

```
Form1.cs                  главное окно и вся логика
Program.cs                точка входа, запрет второго экземпляра
JpegFramer.cs             разбор JPEG-кадров превью
SrtBuild/                 проект второй сборки (SRT)
PROJECT_SPEC.md           техническое описание
README.txt                инструкция для конечного пользователя
```

## Лицензия

GPL v3. Использует GStreamer (LGPL) и MediaMTX (MIT).
