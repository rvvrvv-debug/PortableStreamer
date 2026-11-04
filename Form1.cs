using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PortableStreamer
{
    public partial class Form1 : Form
    {
        // ============================================================================
        //  ПОЛЯ — элементы интерфейса
        // ============================================================================

        private GroupBox gbEncoding = null!;
        private ComboBox txtResolution = null!;
        private ComboBox txtFps = null!;
        private ComboBox txtBitrate = null!;
        private ComboBox txtAudioBitrate = null!;
        private ComboBox txtSampleRate = null!;
        private ComboBox cmbEncoder = null!;
        private Label lblEncHint = null!;

        private GroupBox gbPreview = null!;
        private CheckBox chkPreview = null!;
        private PictureBox picPreview = null!;
        private Label lblPreviewHint = null!;

        private GroupBox gbProfile = null!;
        private ComboBox cmbProfile = null!;
        private Button btnSaveProfile = null!;
        private Button btnDeleteProfile = null!;
        private Button btnNewProfile = null!;

        private GroupBox gbAbout = null!;

        private GroupBox gbSources = null!;
        private CheckBox chkUseIntro = null!;
        private TextBox txtIntroJpg = null!;
        private Button btnBrowseIntro = null!;
        private NumericUpDown numIntroDuration = null!;
        private CheckBox chkUseMainVideo = null!;
        private TextBox txtMainVideo = null!;
        private Button btnBrowseMain = null!;
        private ComboBox cmbLoopSource = null!;
        private Button btnLoopAudio = null!;
        private Label lblMainVideoWarn = null!;
        private CheckBox chkUseOutro = null!;
        private TextBox txtOutroJpg = null!;
        private Button btnBrowseOutro = null!;
        private readonly ToolTip _tip = new();

        private const string ImagesFilter = "Изображения|*.jpg;*.jpeg;*.png|Все файлы|*.*";
        private const string VideoFilter = "Видео|*.mp4;*.mov;*.m4v|Все файлы|*.*";

        private GroupBox gbBroadcast = null!;
        private TextBox txtRtmpUrl = null!;
        private TextBox txtStreamKey = null!;
        private DateTimePicker dtpStartTime = null!;
        private ComboBox cmbMode = null!;
        private Label lblStartTime = null!;
        private Label lblDuration = null!;
        private Label lblMinutes = null!;
        private NumericUpDown numDuration = null!;
        private CheckBox chkClockOverlay = null!;

        private ProgressBar progressTimer = null!;
        private Label lblStatus = null!;
        private Label lblCdnStatus = null!;
        private Label lblCountdown = null!;
        private Button btnStart = null!;
        private Button btnEmergencyStop = null!;
        private Button btnExitProgram = null!;
        private Button btnOpenLogs = null!;
        private Label lblLogsPath = null!;

        private System.Windows.Forms.Timer schedulerTimer = null!;
        private System.Windows.Forms.Timer progressUpdateTimer = null!;

        // ============================================================================
        //  ПОЛЯ — Логика профилей и анализа
        // ============================================================================

        private Dictionary<string, Dictionary<string, string>> _profiles = new();
        private string _currentProfileName = "";
        private bool _isLoadingProfile = false;

        private record struct VideoInfo(
            bool IsValid,
            int DurationSec,
            int Width,
            int Height,
            double Fps,
            int VideoBitrate,
            string VideoCodec,
            string AudioCodec);

        // ============================================================================
        //  ПОЛЯ — Логика плейлиста, процессов и превью
        // ============================================================================

        private CancellationTokenSource? _prepCts;
        private string _selectedEncoder = "x264enc";

        private Process? _mediamtxProcess;
        private Process? _streamProcess;

        private enum SegmentKind { Image, File, Test }

        /// <summary>
        /// Элемент плейлиста: либо картинка (генерируется прямо в эфирном потоке),
        /// либо видеофайл пользователя. HasAudio нужен, чтобы не строить ветку
        /// звука для файла без звуковой дорожки — иначе конвейер не собирается.
        /// </summary>
        private sealed record PlaylistSegment(
            SegmentKind Kind, string Path, int DurationSeconds, bool HasAudio = true,
            bool Tone = false, string? AudioPath = null);

        private readonly List<PlaylistSegment> _playlist = new();

        // Тестовый цикл: выбранный звуковой файл и защита от рекурсии в списке источников.
        private string _loopAudioPath = "";
        private int _lastLoopSourceIndex;
        private bool _suppressLoopSourceChange;

        private DateTime _streamStartTime;
        private DateTime? _scheduledStopAt;
        private bool _stopBySchedule;
        private int _totalDurationSeconds = 0;

        // Вид сборки: обычная (RTMP) или SRT. Задаётся константой компиляции,
        // поэтому исходный код один, а exe получаются два.
        // static readonly, а не const: иначе компилятор считает ветки
        // недостижимыми и предупреждает (CS0162).
#if SRT_BUILD
        private static readonly bool SrtBuild = true;
#else
        private static readonly bool SrtBuild = false;
#endif

        private const string InternalStreamPath = "live/stream";
        private const string InternalStreamUrl = "rtmp://127.0.0.1:1935/live/stream";

        // Превью: отдельный процесс GStreamer (RTSP из MediaMTX) -> JPEG по TCP -> C#-клиент
        private Process? _previewProcess;
        private CancellationTokenSource? _previewCts;
        private int _previewPort = 0;

        private string PrepCacheDir
        {
            get
            {
                var dir = Path.Combine(Path.GetTempPath(), "GstScheduler", "prep");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private string ConfigFilePath
        {
            get
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                string exeConfig = Path.Combine(exeDir, "GstScheduler.cfg");
                try { File.AppendAllText(exeConfig, ""); return exeConfig; }
                catch
                {
                    string tempDir = Path.Combine(Path.GetTempPath(), "GstScheduler");
                    Directory.CreateDirectory(tempDir);
                    return Path.Combine(tempDir, "GstScheduler.cfg");
                }
            }
        }

        private string GstToolsDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "gstreamer");
        private string GstBinDir => Path.Combine(GstToolsDir, "bin");
        private string GstLaunchExe => Path.Combine(GstBinDir, "gst-launch-1.0.exe");
        private string GstInspectExe => Path.Combine(GstBinDir, "gst-inspect-1.0.exe");
        private string GstDiscovererExe => Path.Combine(GstBinDir, "gst-discoverer-1.0.exe");
        private string MediaMtxExe => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "mediamtx.exe");

        private string? _machineId;

        /// <summary>
        /// Короткий идентификатор компьютера (8 символов) для имён логов.
        /// Нужен, чтобы логи с разных машин не путались при сборе.
        /// Хранится в файле machine.id рядом с настройками.
        /// </summary>
        private string MachineId
        {
            get
            {
                if (_machineId != null) return _machineId;

                try
                {
                    string dir = Path.GetDirectoryName(ConfigFilePath)!;
                    string file = Path.Combine(dir, "machine.id");

                    if (File.Exists(file))
                    {
                        string saved = File.ReadAllText(file).Trim();
                        if (saved.Length >= 4)
                        {
                            _machineId = saved;
                            return saved;
                        }
                    }

                    string seed = Environment.MachineName + "|" + Environment.UserName + "|" +
                                  Environment.OSVersion.VersionString;
                    byte[] hash = System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(seed));

                    string id = Convert.ToHexString(hash, 0, 4);   // 8 символов

                    try { File.WriteAllText(file, id); } catch { }

                    _machineId = id;
                    return id;
                }
                catch
                {
                    _machineId = "LOCAL000";
                    return _machineId;
                }
            }
        }

        /// <summary>
        /// Явно задаёт окружение GStreamer для дочернего процесса.
        /// Без этого программа полагается на то, что GStreamer сам найдёт плагины
        /// рядом с собой. Обычно так и происходит, но на чужой машине это неявная
        /// договорённость, которая может не сработать.
        /// </summary>
        private void ApplyGstEnvironment(ProcessStartInfo psi)
        {
            try
            {
                string existingPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                psi.Environment["PATH"] = GstBinDir + ";" + existingPath;

                string plugins = Path.Combine(GstToolsDir, "lib", "gstreamer-1.0");
                if (Directory.Exists(plugins))
                    psi.Environment["GST_PLUGIN_PATH"] = plugins;
            }
            catch { }
        }

        /// <summary>
        /// Короткая проверка энкодера настоящим запуском: аппаратные кодировщики
        /// на разных машинах бывают «есть, но не работают». Лучше узнать это
        /// до эфира, чем из непонятной ошибки конвейера.
        /// </summary>
        private async Task<bool> ProbeEncoderAsync(string encoder)
        {
            if (encoder == "x264enc")
                return true;

            string encArgs = encoder switch
            {
                "nvh264enc" => "nvh264enc bitrate=1000",
                "qsvh264enc" => "qsvh264enc bitrate=1000",
                "amfh264enc" => "amfh264enc bitrate=1000000",
                "msdkh264enc" => "msdkh264enc bitrate=1000",
                _ => ""
            };

            if (encArgs.Length == 0) return false;

            string args =
                "-q videotestsrc num-buffers=5 ! videoconvert " +
                "! video/x-raw,width=640,height=360,framerate=25/1 " +
                $"! {encArgs} ! h264parse ! fakesink sync=false";

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = GstLaunchExe,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = GstBinDir
                };
                ApplyGstEnvironment(psi);

                using var p = new Process { StartInfo = psi };
                if (!p.Start()) return false;

                var errTask = p.StandardError.ReadToEndAsync();
                var outTask = p.StandardOutput.ReadToEndAsync();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await p.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return false;
                }

                await Task.WhenAll(errTask, outTask);
                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Проверяет выбранный энкодер и при неудаче переключается на CPU.
        /// </summary>
        private async Task EnsureEncoderWorksAsync()
        {
            string preferred = _selectedEncoder;
            if (preferred == "x264enc") return;

            SetStatus($"Проверка энкодера {preferred}...", StatusKind.Waiting);

            if (await ProbeEncoderAsync(preferred))
                return;

            _selectedEncoder = "x264enc";

            try
            {
                AppendLog(Path.Combine(PrepCacheDir, "stream_log.txt"),
                    $">>> Энкодер {preferred} не запустился — переключение на x264enc (CPU)");
            }
            catch { }

            lblEncHint.Text = $"⚠ {preferred} не работает — выбран CPU (x264enc)";
            lblEncHint.ForeColor = Color.DarkOrange;
            SetStatus("Аппаратный энкодер недоступен — работаю на CPU", StatusKind.Waiting);
        }

        private enum StatusKind { Idle, Waiting, Intro, Main, Outro, Ok, Error }

        private readonly record struct EncodingSettings(
            int Width, int Height, int Fps, int VideoKbps, int AudioKbps, int SampleRate);

        // ============================================================================
        //  КОНСТРУКТОР
        // ============================================================================

        public Form1()
        {
            // Убираем «сирот» от прошлого запуска: MediaMTX и gst-launch,
            // которые могли остаться, если программу закрыли аварийно.
            KillStaleChildProcesses();

            InitializeComponent();
            SetStatus("Готов", StatusKind.Idle);
            SetPreviewHint("нет эфира");
            lblLogsPath.Text = "Логи: " + PrepCacheDir;
            ApplyMode();
            LoadProfiles();
            _ = DetectGpuAsync();
        }

        // ============================================================================
        //  ИНИЦИАЛИЗАЦИЯ UI
        // ============================================================================

        private void InitializeComponent()
        {
            SuspendLayout();

            Text = SrtBuild
                ? "GstScheduler — Playout Server v.2.02 (SRT)"
                : "GstScheduler — Playout Server v.2.02";
            ClientSize = new Size(950, 550);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = SystemColors.Control;
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.None;

            // ===== Параметры кодирования =====
            gbEncoding = MkGroup("Параметры кодирования", 15, 10, 720, 100);
            Controls.Add(gbEncoding);

            // Поля параметров — списки с общепринятыми значениями, но ввести своё
            // тоже можно: DropDown, а не DropDownList.
            gbEncoding.Controls.Add(MkLabel("Разрешение:", 10, 22, 75));
            txtResolution = MkValueCombo(88, 20, 110, "1920x1080",
                "1920x1080", "1280x720", "854x480", "1080x1920", "720x1280", "2560x1440");
            _tip.SetToolTip(txtResolution, "Ширина x Высота. Можно ввести своё значение");
            gbEncoding.Controls.Add(txtResolution);

            gbEncoding.Controls.Add(MkLabel("FPS:", 206, 22, 30));
            txtFps = MkValueCombo(238, 20, 55, "25", "25", "30", "50", "60", "24");
            _tip.SetToolTip(txtFps, "Кадров в секунду");
            gbEncoding.Controls.Add(txtFps);

            gbEncoding.Controls.Add(MkLabel("Видео:", 301, 22, 45));
            txtBitrate = MkValueCombo(348, 20, 85, "4200", "4200", "2500", "6000", "8000", "12000");
            _tip.SetToolTip(txtBitrate, "Битрейт видео, кбит/с");
            gbEncoding.Controls.Add(txtBitrate);

            gbEncoding.Controls.Add(MkLabel("Аудио:", 441, 22, 45));
            txtAudioBitrate = MkValueCombo(488, 20, 85, "192", "128", "192", "256", "320", "96");
            _tip.SetToolTip(txtAudioBitrate, "Битрейт аудио, кбит/с");
            gbEncoding.Controls.Add(txtAudioBitrate);

            gbEncoding.Controls.Add(MkLabel("Hz:", 581, 22, 25));
            txtSampleRate = MkValueCombo(608, 20, 85, "48000", "48000", "44100");
            _tip.SetToolTip(txtSampleRate, "Частота дискретизации, Гц");
            gbEncoding.Controls.Add(txtSampleRate);

            gbEncoding.Controls.Add(MkLabel("Кодек:", 10, 52, 50));
            cmbEncoder = new ComboBox
            {
                Location = new Point(62, 50),
                Size = new Size(180, 22),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbEncoder.Items.Add("Авто (лучший доступный)");
            cmbEncoder.Items.Add("Только CPU (x264enc)");
            cmbEncoder.SelectedIndex = 0;
            cmbEncoder.SelectedIndexChanged += cmbEncoder_SelectedIndexChanged;
            gbEncoding.Controls.Add(cmbEncoder);

            lblEncHint = new Label
            {
                Text = "Определение GPU...",
                Location = new Point(345, 52),
                Size = new Size(360, 20),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.DimGray,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 9F, FontStyle.Underline)
            };
            lblEncHint.Click += lblEncHint_Click;
            gbEncoding.Controls.Add(lblEncHint);

            // ===== Медиа-источники =====
            gbSources = MkGroup("Медиа-источники плейлиста", 15, 120, 720, 145);
            Controls.Add(gbSources);

            chkUseIntro = new CheckBox { Text = "Intro JPG:", Location = new Point(10, 22), Size = new Size(100, 20) };
            chkUseIntro.CheckedChanged += chkUseIntro_CheckedChanged;
            gbSources.Controls.Add(chkUseIntro);

            txtIntroJpg = MkText("", 115, 20, 330);
            txtIntroJpg.Enabled = false;
            EnableFileDrop(txtIntroJpg, isVideo: false);
            gbSources.Controls.Add(txtIntroJpg);

            btnBrowseIntro = MkBrowseButton(450, 20);
            btnBrowseIntro.Enabled = false;
            btnBrowseIntro.Click += (_, _) => BrowseFile(txtIntroJpg, ImagesFilter);
            gbSources.Controls.Add(btnBrowseIntro);

            numIntroDuration = new NumericUpDown
            {
                Minimum = 40,
                Maximum = 3600,
                // 60 секунд по умолчанию — как в ТЗ. Меньше 40 сек. ставить нельзя:
                // CDNvideo тратит ~40 секунд на подъём и стабилизацию потока,
                // и intro как раз прикрывает это время.
                Value = 60,
                Increment = 5,
                Location = new Point(492, 20),
                Size = new Size(70, 22),
                Enabled = false
            };
            gbSources.Controls.Add(numIntroDuration);

            gbSources.Controls.Add(MkLabel("сек", 566, 22, 30));

            chkUseMainVideo = new CheckBox
            {
                Text = "Main Video:",
                Location = new Point(10, 48),
                Size = new Size(100, 20),
                Checked = true
            };
            chkUseMainVideo.CheckedChanged += chkUseMainVideo_CheckedChanged;
            gbSources.Controls.Add(chkUseMainVideo);

            txtMainVideo = MkText("", 115, 46, 330);
            txtMainVideo.Leave += txtMainVideo_Leave;
            EnableFileDrop(txtMainVideo, isVideo: true);
            gbSources.Controls.Add(txtMainVideo);

            btnBrowseMain = MkBrowseButton(450, 46);
            btnBrowseMain.Click += (_, _) =>
            {
                // В цикле с картинкой выбираем изображение, иначе — видео.
                bool image = LoopSourceIsImage;
                BrowseFile(txtMainVideo, image ? ImagesFilter : VideoFilter, runAnalysis: !image);
            };
            gbSources.Controls.Add(btnBrowseMain);

            // Источник цикла — только для режима «Тестовый цикл».
            cmbLoopSource = new ComboBox
            {
                Location = new Point(492, 46),
                Size = new Size(215, 22),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Visible = false
            };
            cmbLoopSource.Items.Add("Цикл: файл Main Video");
            cmbLoopSource.Items.Add("Цикл: картинка + тон 1 кГц");
            cmbLoopSource.Items.Add("Цикл: картинка + MP3");
            cmbLoopSource.Items.Add("Цикл: тестовая таблица");
            cmbLoopSource.SelectedIndex = 0;
            cmbLoopSource.SelectedIndexChanged += cmbLoopSource_SelectedIndexChanged;
            gbSources.Controls.Add(cmbLoopSource);

            // Кнопка смены звукового файла для «картинка + MP3».
            btnLoopAudio = new Button
            {
                Text = "…",
                Location = new Point(709, 46),
                Size = new Size(24, 22),
                Visible = false
            };
            _tip.SetToolTip(btnLoopAudio, "Выбрать другой звуковой файл");
            btnLoopAudio.Click += (_, _) =>
            {
                if (AskLoopAudioFile())
                    UpdateLoopSourceTooltip();
            };
            gbSources.Controls.Add(btnLoopAudio);

            lblMainVideoWarn = new Label
            {
                Text = "",
                Location = new Point(10, 74),
                Size = new Size(700, 20),
                ForeColor = Color.DimGray
            };
            gbSources.Controls.Add(lblMainVideoWarn);

            chkUseOutro = new CheckBox { Text = "Outro JPG:", Location = new Point(10, 100), Size = new Size(100, 20) };
            chkUseOutro.CheckedChanged += chkUseOutro_CheckedChanged;
            gbSources.Controls.Add(chkUseOutro);

            txtOutroJpg = MkText("", 115, 98, 330);
            txtOutroJpg.Enabled = false;
            EnableFileDrop(txtOutroJpg, isVideo: false);
            gbSources.Controls.Add(txtOutroJpg);

            btnBrowseOutro = MkBrowseButton(450, 98);
            btnBrowseOutro.Enabled = false;
            btnBrowseOutro.Click += (_, _) => BrowseFile(txtOutroJpg, ImagesFilter);
            gbSources.Controls.Add(btnBrowseOutro);

            // ===== Настройки трансляции =====
            gbBroadcast = MkGroup("Настройки трансляции и времени старта", 15, 275, 720, 95);
            Controls.Add(gbBroadcast);

            // RTMP URL и Stream Key — одинаковой ширины. Полный текст поля
            // показывается во всплывающей подсказке: ключи бывают длинными
            // и нужны для визуального контроля вставки.
            // В SRT-сборке те же поля означают адрес SRT и Stream ID.
            string urlCaption = SrtBuild ? "SRT адрес:" : "RTMP URL:";
            string keyCaption = SrtBuild ? "Stream ID:" : "Stream Key:";

            gbBroadcast.Controls.Add(MkLabel(urlCaption, 10, 22, 70));
            txtRtmpUrl = MkText(SrtBuild ? "srt://" : "rtmp://", 82, 20, 265);
            txtRtmpUrl.TextChanged += (_, _) => _tip.SetToolTip(txtRtmpUrl, txtRtmpUrl.Text);
            _tip.SetToolTip(txtRtmpUrl, SrtBuild
                ? "Адрес приёмника SRT: srt://хост:порт"
                : "Адрес RTMP-сервера CDN");
            gbBroadcast.Controls.Add(txtRtmpUrl);

            gbBroadcast.Controls.Add(MkLabel(keyCaption, 357, 22, 80));
            txtStreamKey = MkText("", 439, 20, 265);
            txtStreamKey.TextChanged += (_, _) => _tip.SetToolTip(txtStreamKey, txtStreamKey.Text);
            _tip.SetToolTip(txtStreamKey, SrtBuild
                ? "Stream ID приёмника (если он его требует). Можно оставить пустым"
                : "Ключ потока из личного кабинета провайдера");
            gbBroadcast.Controls.Add(txtStreamKey);

            // Режим работы: обычный эфир по расписанию или тестовый цикл.
            gbBroadcast.Controls.Add(MkLabel("Режим:", 10, 50, 52));
            cmbMode = new ComboBox
            {
                Location = new Point(64, 48),
                Size = new Size(150, 22),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbMode.Items.Add("Эфир по расписанию");
            cmbMode.Items.Add("Тестовый цикл");
            cmbMode.SelectedIndex = 0;
            cmbMode.SelectedIndexChanged += (_, _) => ApplyMode();
            gbBroadcast.Controls.Add(cmbMode);

            lblStartTime = MkLabel("Время старта:", 226, 50, 88);
            gbBroadcast.Controls.Add(lblStartTime);

            dtpStartTime = new DateTimePicker
            {
                Format = DateTimePickerFormat.Custom,
                CustomFormat = "dd.MM.yyyy HH:mm:ss",
                Location = new Point(318, 48),
                Size = new Size(200, 22),
                Value = DateTime.Now.AddMinutes(2)
            };
            gbBroadcast.Controls.Add(dtpStartTime);

            lblDuration = MkLabel("Длительность:", 226, 50, 88);
            lblDuration.Visible = false;
            gbBroadcast.Controls.Add(lblDuration);

            numDuration = new NumericUpDown
            {
                Location = new Point(318, 48),
                Size = new Size(70, 22),
                Minimum = 0,
                Maximum = 1440,
                Value = 30,
                Increment = 5,
                Visible = false
            };
            _tip.SetToolTip(numDuration, "Минуты. 0 = работать до ручной остановки");
            gbBroadcast.Controls.Add(numDuration);

            lblMinutes = MkLabel("мин (0 = до остановки)", 392, 50, 170);
            lblMinutes.Visible = false;
            gbBroadcast.Controls.Add(lblMinutes);

            chkClockOverlay = new CheckBox
            {
                Text = "Часы на экране",
                Location = new Point(560, 50),
                Size = new Size(150, 20),
                Visible = false
            };
            _tip.SetToolTip(chkClockOverlay, "Показывать текущее время на картинке — для замера задержки CDN");
            gbBroadcast.Controls.Add(chkClockOverlay);

            // ===== Мониторинг =====
            progressTimer = new ProgressBar { Location = new Point(15, 385), Size = new Size(620, 25) };
            Controls.Add(progressTimer);

            lblCountdown = new Label
            {
                Text = "00:00",
                Location = new Point(640, 383),
                Size = new Size(95, 30),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Consolas", 14F, FontStyle.Bold),
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(lblCountdown);

            lblStatus = new Label
            {
                Text = "Готов",
                Location = new Point(15, 415),
                Size = new Size(720, 24),
                TextAlign = ContentAlignment.MiddleCenter,
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(lblStatus);

            btnStart = MkButton("ПУСК", 15, 445, 233, 40, Color.LightGreen);
            btnStart.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            btnStart.Click += btnStart_Click;
            Controls.Add(btnStart);

            btnEmergencyStop = MkButton("АВАРИЙНЫЙ СТОП", 253, 445, 233, 40, Color.Crimson);
            btnEmergencyStop.ForeColor = Color.White;
            btnEmergencyStop.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            btnEmergencyStop.Click += btnEmergencyStop_Click;
            Controls.Add(btnEmergencyStop);

            btnExitProgram = MkButton("ВЫХОД ИЗ ПРОГРАММЫ", 491, 445, 244, 40, Color.Gainsboro);
            btnExitProgram.Click += btnExitProgram_Click;
            Controls.Add(btnExitProgram);

            // Кнопка для поиска логов: на чужой машине %TEMP% искать утомительно.
            btnOpenLogs = MkButton("Открыть логи", 15, 495, 160, 28, Color.Gainsboro);
            btnOpenLogs.Click += (_, _) => OpenLogsFolder();
            Controls.Add(btnOpenLogs);

            lblLogsPath = new Label
            {
                Text = "",
                Location = new Point(185, 495),
                Size = new Size(550, 28),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.DimGray,
                Font = new Font("Segoe UI", 8F)
            };
            Controls.Add(lblLogsPath);

            // ===== Превью =====
            gbPreview = MkGroup("Превью эфира", 750, 10, 185, 145);
            Controls.Add(gbPreview);

            chkPreview = new CheckBox { Text = "Показывать", Location = new Point(10, 22), Size = new Size(160, 20) };
            chkPreview.CheckedChanged += chkPreview_CheckedChanged;
            gbPreview.Controls.Add(chkPreview);

            picPreview = new PictureBox
            {
                Location = new Point(10, 46),
                Size = new Size(160, 90),
                BackColor = Color.Black,
                BorderStyle = BorderStyle.FixedSingle,
                SizeMode = PictureBoxSizeMode.Zoom
            };

            // Рамка превью видна всегда. Пока картинки нет, на чёрном поле
            // показывается состояние эфира, а не пустота.
            lblPreviewHint = new Label
            {
                Text = "нет эфира",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Black,
                ForeColor = Color.Silver,
                Font = new Font("Segoe UI", 9F)
            };
            picPreview.Controls.Add(lblPreviewHint);
            gbPreview.Controls.Add(picPreview);

            // Плашка состояния CDN — под блоком превью, во всю его ширину.
            lblCdnStatus = new Label
            {
                Text = "CDN: —",
                Location = new Point(750, 160),
                Size = new Size(185, 24),
                TextAlign = ContentAlignment.MiddleCenter,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 9F),
                BackColor = Color.Gainsboro,
                ForeColor = Color.DimGray
            };
            Controls.Add(lblCdnStatus);

            // ===== Профиль =====
            gbProfile = MkGroup("Профиль трансляции", 750, 190, 185, 130);
            Controls.Add(gbProfile);

            cmbProfile = new ComboBox
            {
                Location = new Point(10, 20),
                Size = new Size(165, 22),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbProfile.SelectedIndexChanged += cmbProfile_SelectedIndexChanged;
            gbProfile.Controls.Add(cmbProfile);

            btnSaveProfile = new Button { Text = "Сохранить", Location = new Point(10, 48), Size = new Size(80, 25) };
            btnSaveProfile.Click += btnSaveProfile_Click;
            gbProfile.Controls.Add(btnSaveProfile);

            btnDeleteProfile = new Button { Text = "Удалить", Location = new Point(95, 48), Size = new Size(80, 25), Enabled = false };
            btnDeleteProfile.Click += btnDeleteProfile_Click;
            gbProfile.Controls.Add(btnDeleteProfile);

            btnNewProfile = new Button { Text = "Создать новый...", Location = new Point(10, 78), Size = new Size(165, 25) };
            btnNewProfile.Click += btnNewProfile_Click;
            gbProfile.Controls.Add(btnNewProfile);

            // ===== О программе =====
            gbAbout = MkGroup("О программе", 750, 328, 185, 195);
            Controls.Add(gbAbout);

            int by = 22;
            gbAbout.Controls.Add(new Label { Text = "Автор: Виктор Решедько (RVic)", Location = new Point(10, by), Size = new Size(170, 18) }); by += 20;
            gbAbout.Controls.Add(new Label { Text = "При участии: Claude (Anthropic)", Location = new Point(10, by), Size = new Size(170, 18) }); by += 20;
            gbAbout.Controls.Add(new Label { Text = "Лицензия: GPL v3", Location = new Point(10, by), Size = new Size(170, 18) }); by += 26;

            var lnkGst = new LinkLabel { Text = "gstreamer.freedesktop.org", Location = new Point(10, by), Size = new Size(170, 18) };
            lnkGst.LinkClicked += (_, _) => OpenUrl("https://gstreamer.freedesktop.org");
            gbAbout.Controls.Add(lnkGst); by += 20;

            var lnkGpl = new LinkLabel { Text = "gnu.org/licenses/gpl-3.0", Location = new Point(10, by), Size = new Size(170, 18) };
            lnkGpl.LinkClicked += (_, _) => OpenUrl("https://www.gnu.org/licenses/gpl-3.0.html");
            gbAbout.Controls.Add(lnkGpl); by += 20;

            var lnkAi = new LinkLabel { Text = "anthropic.com/claude", Location = new Point(10, by), Size = new Size(170, 18) };
            lnkAi.LinkClicked += (_, _) => OpenUrl("https://www.anthropic.com/claude");
            gbAbout.Controls.Add(lnkAi);

            schedulerTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            schedulerTimer.Tick += schedulerTimer_Tick;

            progressUpdateTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            progressUpdateTimer.Tick += ProgressUpdateTimer_Tick;

            FormClosing += Form1_FormClosing;

            ResumeLayout(false);
        }

        // ============================================================================
        //  ФАБРИКИ КОНТРОЛОВ
        // ============================================================================

        private static Label MkLabel(string text, int x, int y, int w, int h = 20) => new Label
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, h),
            TextAlign = ContentAlignment.MiddleLeft
        };

        private static TextBox MkText(string text, int x, int y, int w) => new TextBox
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, 22)
        };

        /// <summary>
        /// Поле со списком типовых значений, но с возможностью ввести своё.
        /// </summary>
        private static ComboBox MkValueCombo(int x, int y, int w, string text, params string[] presets)
        {
            var cmb = new ComboBox
            {
                Location = new Point(x, y),
                Size = new Size(w, 22),
                DropDownStyle = ComboBoxStyle.DropDown
            };

            foreach (var preset in presets)
                cmb.Items.Add(preset);

            cmb.Text = text;
            return cmb;
        }

        private static GroupBox MkGroup(string title, int x, int y, int w, int h) => new GroupBox
        {
            Text = title,
            Location = new Point(x, y),
            Size = new Size(w, h)
        };

        private static Button MkButton(string text, int x, int y, int w, int h, Color back) => new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, h),
            BackColor = back,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false
        };

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
            catch { }
        }

        // ============================================================================
        //  ВЫБОР ФАЙЛОВ: кнопка "..." и перетаскивание в поле
        // ============================================================================

        private static Button MkBrowseButton(int x, int y) => new Button
        {
            Text = "...",
            Location = new Point(x, y),
            Size = new Size(32, 22)
        };

        private void BrowseFile(TextBox target, string filter, bool runAnalysis = false)
        {
            using var dlg = new OpenFileDialog
            {
                Filter = filter,
                CheckFileExists = true,
                Multiselect = false,
                Title = "Выберите файл"
            };

            if (!string.IsNullOrWhiteSpace(target.Text) && File.Exists(target.Text))
                dlg.FileName = target.Text;

            if (dlg.ShowDialog(this) != DialogResult.OK)
                return;

            target.Text = dlg.FileName;

            if (runAnalysis)
                _ = UpdateMainVideoInfoAsync();
        }

        /// <summary>
        /// Включает перетаскивание файла из Проводника прямо в текстовое поле.
        /// </summary>
        private void EnableFileDrop(TextBox box, bool isVideo)
        {
            box.AllowDrop = true;

            box.DragEnter += (_, e) =>
            {
                e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
                    ? DragDropEffects.Copy
                    : DragDropEffects.None;
            };

            box.DragDrop += (_, e) =>
            {
                if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
                    return;

                box.Text = files[0];

                // Анализ видео не нужен, если это картинка для тестового цикла.
                if (isVideo && !LoopSourceIsImage)
                    _ = UpdateMainVideoInfoAsync();
            };
        }

        // ============================================================================
        //  ПРОФИЛИ (INI-логика)
        // ============================================================================

        private void LoadProfiles()
        {
            _profiles.Clear();
            string path = ConfigFilePath;

            if (File.Exists(path))
            {
                string[] lines = File.ReadAllLines(path);
                string currentSection = "";
                foreach (var line in lines)
                {
                    string trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(";")) continue;

                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        currentSection = trimmed.Substring(1, trimmed.Length - 2);
                        if (!_profiles.ContainsKey(currentSection))
                            _profiles[currentSection] = new Dictionary<string, string>();
                    }
                    else if (trimmed.Contains("=") && !string.IsNullOrEmpty(currentSection))
                    {
                        var parts = trimmed.Split('=', 2);
                        string key = parts[0].Trim();
                        string val = parts.Length > 1 ? parts[1].Trim() : "";
                        _profiles[currentSection][key] = val;
                    }
                }
            }

            if (_profiles.Count == 0)
                _profiles["Default"] = new Dictionary<string, string>();

            _isLoadingProfile = true;
            cmbProfile.Items.Clear();
            foreach (var key in _profiles.Keys) cmbProfile.Items.Add(key);
            cmbProfile.SelectedIndex = 0;
            _isLoadingProfile = false;

            _currentProfileName = cmbProfile.Text;
            ApplyProfile(_currentProfileName);
            UpdateProfileButtons();
        }

        private void SaveProfilesToDisk()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var kvp in _profiles)
                {
                    sb.AppendLine($"[{kvp.Key}]");
                    foreach (var val in kvp.Value) sb.AppendLine($"{val.Key}={val.Value}");
                    sb.AppendLine();
                }
                File.WriteAllText(ConfigFilePath, sb.ToString());
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка сохранения профиля:\n" + ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private Dictionary<string, string> GetCurrentProfileValues()
        {
            return new Dictionary<string, string>
            {
                { "Resolution", txtResolution.Text },
                { "Fps", txtFps.Text },
                { "VideoKbps", txtBitrate.Text },
                { "AudioKbps", txtAudioBitrate.Text },
                { "SampleRate", txtSampleRate.Text },
                { "RtmpUrl", txtRtmpUrl.Text },
                { "StreamKey", txtStreamKey.Text },
                { "LoopAudio", _loopAudioPath }
            };
        }

        private void ApplyProfile(string name)
        {
            if (!_profiles.TryGetValue(name, out var profile)) return;

            _isLoadingProfile = true;
            txtResolution.Text = profile.GetValueOrDefault("Resolution", "1920x1080");
            txtFps.Text = profile.GetValueOrDefault("Fps", "25");
            txtBitrate.Text = profile.GetValueOrDefault("VideoKbps", "4200");
            txtAudioBitrate.Text = profile.GetValueOrDefault("AudioKbps", "192");
            txtSampleRate.Text = profile.GetValueOrDefault("SampleRate", "48000");
            // В SRT-сборке адрес должен быть srt://, и наоборот: профиль общий,
            // и без этой проверки SRT-версия показывала бы rtmp:// из настроек
            // обычной версии (и наоборот).
            string savedUrl = profile.GetValueOrDefault("RtmpUrl", SrtBuild ? "srt://" : "rtmp://");

            if (SrtBuild && !savedUrl.StartsWith("srt://", StringComparison.OrdinalIgnoreCase))
                savedUrl = "srt://";
            else if (!SrtBuild && savedUrl.StartsWith("srt://", StringComparison.OrdinalIgnoreCase))
                savedUrl = "rtmp://";

            txtRtmpUrl.Text = savedUrl;
            txtStreamKey.Text = profile.GetValueOrDefault("StreamKey", "");
            _loopAudioPath = profile.GetValueOrDefault("LoopAudio", "");
            UpdateLoopSourceCaption();
            _isLoadingProfile = false;
        }

        private void UpdateProfileButtons() => btnDeleteProfile.Enabled = _profiles.Count > 1;

        private void cmbProfile_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (_isLoadingProfile) return;

            if (!string.IsNullOrEmpty(_currentProfileName) && _profiles.ContainsKey(_currentProfileName))
            {
                _profiles[_currentProfileName] = GetCurrentProfileValues();
                SaveProfilesToDisk();
            }

            _currentProfileName = cmbProfile.Text;
            ApplyProfile(_currentProfileName);
        }

        private void btnSaveProfile_Click(object? sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_currentProfileName)) return;
            _profiles[_currentProfileName] = GetCurrentProfileValues();
            SaveProfilesToDisk();
            MessageBox.Show("Профиль сохранен.", "Успех", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void btnDeleteProfile_Click(object? sender, EventArgs e)
        {
            if (_profiles.Count <= 1) return;
            if (MessageBox.Show($"Удалить профиль '{_currentProfileName}'?", "Подтверждение",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _profiles.Remove(_currentProfileName);
                SaveProfilesToDisk();
                LoadProfiles();
            }
        }

        private void btnNewProfile_Click(object? sender, EventArgs e)
        {
            string? name = ShowInputDialog("Введите имя нового профиля:", "Новый профиль", "Новый профиль");
            if (name == null) return;

            if (_profiles.ContainsKey(name))
            {
                MessageBox.Show("Профиль с таким именем уже существует.", "Ошибка",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _profiles[name] = new Dictionary<string, string>();
            SaveProfilesToDisk();

            _isLoadingProfile = true;
            cmbProfile.Items.Add(name);
            cmbProfile.SelectedItem = name;
            _isLoadingProfile = false;

            _currentProfileName = name;
            ApplyProfile(name);
            UpdateProfileButtons();
        }

        private string? ShowInputDialog(string prompt, string title, string defaultValue)
        {
            using var dlg = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(400, 120),
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                BackColor = Color.WhiteSmoke
            };

            var lbl = new Label
            {
                Text = prompt,
                Location = new Point(15, 15),
                Size = new Size(370, 20),
                TextAlign = ContentAlignment.MiddleLeft
            };

            var txt = new TextBox
            {
                Text = defaultValue,
                Location = new Point(15, 40),
                Size = new Size(370, 22)
            };

            var btnOk = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(220, 80),
                Size = new Size(80, 28)
            };

            var btnCancel = new Button
            {
                Text = "Отмена",
                DialogResult = DialogResult.Cancel,
                Location = new Point(305, 80),
                Size = new Size(80, 28)
            };

            dlg.Controls.Add(lbl);
            dlg.Controls.Add(txt);
            dlg.Controls.Add(btnOk);
            dlg.Controls.Add(btnCancel);
            dlg.AcceptButton = btnOk;
            dlg.CancelButton = btnCancel;

            return dlg.ShowDialog(this) == DialogResult.OK ? txt.Text : null;
        }

        // ============================================================================
        //  СТАТУС
        // ============================================================================

        private void SetStatus(string text, StatusKind kind)
        {
            lblStatus.Text = text;
            switch (kind)
            {
                case StatusKind.Idle: lblStatus.BackColor = Color.Gainsboro; lblStatus.ForeColor = Color.DimGray; break;
                case StatusKind.Waiting: lblStatus.BackColor = Color.LightSteelBlue; lblStatus.ForeColor = Color.MidnightBlue; break;
                case StatusKind.Intro: lblStatus.BackColor = Color.LightSkyBlue; lblStatus.ForeColor = Color.DarkBlue; break;
                case StatusKind.Main: lblStatus.BackColor = Color.MediumSeaGreen; lblStatus.ForeColor = Color.White; break;
                case StatusKind.Outro: lblStatus.BackColor = Color.SandyBrown; lblStatus.ForeColor = Color.DarkRed; break;
                case StatusKind.Ok: lblStatus.BackColor = Color.PaleGreen; lblStatus.ForeColor = Color.DarkGreen; break;
                case StatusKind.Error: lblStatus.BackColor = Color.Crimson; lblStatus.ForeColor = Color.White; break;
            }
        }

        /// <summary>
        /// Безопасно меняет строку состояния из фонового потока
        /// (сообщения MediaMTX приходят не из UI-потока).
        /// </summary>
        private void SetStatusSafe(string text, StatusKind kind)
        {
            if (IsDisposed) return;

            try
            {
                if (lblStatus.IsHandleCreated)
                    lblStatus.BeginInvoke((Action)(() => SetStatus(text, kind)));
            }
            catch { }
        }

        // ============================================================================
        //  ДЕТЕКТ GPU
        // ============================================================================

        private bool CheckGstElement(string elementName)
        {
            try
            {
                if (!File.Exists(GstInspectExe)) return false;

                var psi = new ProcessStartInfo
                {
                    FileName = GstInspectExe,
                    Arguments = elementName,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = GstBinDir
                };
                ApplyGstEnvironment(psi);

                using var p = Process.Start(psi);
                if (p == null) return false;

                string output = p.StandardOutput.ReadToEnd();
                _ = p.StandardError.ReadToEnd();
                p.WaitForExit();

                return p.ExitCode == 0 && output.IndexOf(elementName, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        private async Task DetectGpuAsync()
        {
            lblEncHint.ForeColor = Color.DimGray;
            lblEncHint.Text = "Определение GPU...";

            var found = new List<string>();
            bool nv = false, qsv = false, amf = false, msdk = false;

            await Task.Run(() =>
            {
                nv = CheckGstElement("nvh264enc");
                qsv = CheckGstElement("qsvh264enc");
                amf = CheckGstElement("amfh264enc");
                msdk = CheckGstElement("msdkh264enc");
            });

            if (nv) found.Add("NVIDIA (NVENC)");
            if (qsv) found.Add("Intel QuickSync");
            if (amf) found.Add("AMD AMF");
            if (msdk) found.Add("Intel MediaSDK");

            string prev = cmbEncoder.Text;
            cmbEncoder.Items.Clear();
            cmbEncoder.Items.Add("Авто (лучший доступный)");
            cmbEncoder.Items.Add("Только CPU (x264enc)");
            if (nv) cmbEncoder.Items.Add("nvh264enc (NVIDIA GPU)");
            if (qsv) cmbEncoder.Items.Add("qsvh264enc (Intel QuickSync)");
            if (amf) cmbEncoder.Items.Add("amfh264enc (AMD AMF)");
            if (msdk) cmbEncoder.Items.Add("msdkh264enc (Intel MediaSDK)");

            int idx = cmbEncoder.Items.IndexOf(prev);
            cmbEncoder.SelectedIndex = idx >= 0 ? idx : 0;

            if (found.Count > 0)
            {
                lblEncHint.Text = "✓ Доступно: " + string.Join(", ", found);
                lblEncHint.ForeColor = Color.Green;
            }
            else
            {
                lblEncHint.Text = "⚠ GPU-энкодеры не найдены — только CPU";
                lblEncHint.ForeColor = Color.Red;
            }

            UpdateSelectedEncoder();
        }

        private void UpdateSelectedEncoder()
        {
            string sel = cmbEncoder.Text;
            if (sel.StartsWith("Авто", StringComparison.OrdinalIgnoreCase))
            {
                if (CheckGstElement("nvh264enc")) _selectedEncoder = "nvh264enc";
                else if (CheckGstElement("qsvh264enc")) _selectedEncoder = "qsvh264enc";
                else if (CheckGstElement("amfh264enc")) _selectedEncoder = "amfh264enc";
                else if (CheckGstElement("msdkh264enc")) _selectedEncoder = "msdkh264enc";
                else _selectedEncoder = "x264enc";
            }
            else if (sel.IndexOf("nvh264enc", StringComparison.OrdinalIgnoreCase) >= 0) _selectedEncoder = "nvh264enc";
            else if (sel.IndexOf("qsvh264enc", StringComparison.OrdinalIgnoreCase) >= 0) _selectedEncoder = "qsvh264enc";
            else if (sel.IndexOf("amfh264enc", StringComparison.OrdinalIgnoreCase) >= 0) _selectedEncoder = "amfh264enc";
            else if (sel.IndexOf("msdkh264enc", StringComparison.OrdinalIgnoreCase) >= 0) _selectedEncoder = "msdkh264enc";
            else _selectedEncoder = "x264enc";
        }

        private void cmbEncoder_SelectedIndexChanged(object? sender, EventArgs e) => UpdateSelectedEncoder();
        private void lblEncHint_Click(object? sender, EventArgs e) => _ = DetectGpuAsync();

        // ============================================================================
        //  АНАЛИЗ ВИДЕО (Main Video)
        // ============================================================================

        private async void txtMainVideo_Leave(object? sender, EventArgs e)
        {
            // В режимах «картинка + …» это изображение, а не видео — анализ не нужен.
            if (LoopSourceIsImage) return;
            await UpdateMainVideoInfoAsync();
        }

        private async Task UpdateMainVideoInfoAsync()
        {
            string path = txtMainVideo.Text.Trim();
            if (string.IsNullOrEmpty(path)) { lblMainVideoWarn.Text = ""; return; }

            // В тестовом цикле с картинкой анализ видео бессмысленен.
            if (LoopSourceIsImage)
            {
                lblMainVideoWarn.Text = File.Exists(path)
                    ? "Изображение для цикла: " + Path.GetFileName(path)
                    : "Изображение не найдено — будет использована встроенная заставка";
                lblMainVideoWarn.ForeColor = Color.DimGray;
                return;
            }

            if (!File.Exists(path))
            {
                lblMainVideoWarn.Text = "Файл не найден!";
                lblMainVideoWarn.ForeColor = Color.Red;
                return;
            }

            lblMainVideoWarn.Text = "Анализ файла...";
            lblMainVideoWarn.ForeColor = Color.DimGray;

            var info = await AnalyzeVideoAsync(path);
            if (!info.IsValid)
            {
                lblMainVideoWarn.Text = "Не удалось прочитать файл или формат не поддерживается.";
                lblMainVideoWarn.ForeColor = Color.Red;
                return;
            }

            if (!TryGetEncodingSettings(out EncodingSettings s, out string settingsError))
            {
                lblMainVideoWarn.Text = "Проверка невозможна: " + settingsError;
                lblMainVideoWarn.ForeColor = Color.DimGray;
                return;
            }

            bool isH264 = info.VideoCodec.Contains("H.264", StringComparison.OrdinalIgnoreCase)
                          || info.VideoCodec.Contains("AVC", StringComparison.OrdinalIgnoreCase);
            bool isAac = info.AudioCodec.Contains("AAC", StringComparison.OrdinalIgnoreCase);
            bool sizeMatches = info.Width == s.Width && info.Height == s.Height;
            bool fpsMatches = Math.Abs(info.Fps - s.Fps) < 0.1;
            bool bitrateMatches = info.VideoBitrate <= s.VideoKbps * 1.2;

            lblMainVideoWarn.Text =
                $"Длительность: {TimeSpan.FromSeconds(info.DurationSec):hh\\:mm\\:ss} | " +
                $"{info.Width}x{info.Height} @ {info.Fps:F2} fps | " +
                $"Видео: {info.VideoCodec} ({info.VideoBitrate} kbps) | " +
                $"Аудио: {info.AudioCodec}";

            if (isH264 && isAac && sizeMatches && fpsMatches && bitrateMatches)
                lblMainVideoWarn.ForeColor = Color.Green;
            else if (isH264 && isAac && !sizeMatches)
                lblMainVideoWarn.ForeColor = Color.Olive;
            else
                lblMainVideoWarn.ForeColor = Color.DarkRed;
        }

        private async Task<VideoInfo> AnalyzeVideoAsync(string filePath)
        {
            if (!File.Exists(filePath) || !File.Exists(GstDiscovererExe))
                return new VideoInfo(false, 0, 0, 0, 0, 0, "", "");

            string safePath = filePath.Replace('\\', '/');
            var psi = new ProcessStartInfo
            {
                FileName = GstDiscovererExe,
                Arguments = $"\"{safePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = GstBinDir
            };
            ApplyGstEnvironment(psi);

            try
            {
                using var p = Process.Start(psi);
                if (p == null) return new VideoInfo(false, 0, 0, 0, 0, 0, "", "");

                string output = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();
                if (p.ExitCode != 0) return new VideoInfo(false, 0, 0, 0, 0, 0, "", "");

                int durationSec = 0, width = 0, height = 0, bitrate = 0;
                double fps = 0;
                string videoCodec = "", audioCodec = "";
                bool inVideoStream = false;

                foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string t = line.Trim();
                    if (t.StartsWith("Duration:"))
                    {
                        // Формат "0:05:23.120000000". Неизвестную/битую длительность просто игнорируем.
                        var parts = t.Substring(9).Trim().Split(':');
                        if (parts.Length == 3
                            && int.TryParse(parts[0], out int hh)
                            && int.TryParse(parts[1], out int mm)
                            && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double ss))
                        {
                            durationSec = hh * 3600 + mm * 60 + (int)ss;
                        }
                    }
                    else if (t.StartsWith("video #"))
                    {
                        inVideoStream = true;
                        videoCodec = t.Substring(t.IndexOf(':') + 1).Trim();
                    }
                    else if (t.StartsWith("audio #"))
                    {
                        inVideoStream = false;
                        audioCodec = t.Substring(t.IndexOf(':') + 1).Trim();
                    }
                    else if (t.StartsWith("Width:") && inVideoStream)
                    {
                        int.TryParse(t.Substring(6).Trim(), out width);
                    }
                    else if (t.StartsWith("Height:") && inVideoStream)
                    {
                        int.TryParse(t.Substring(7).Trim(), out height);
                    }
                    else if (t.StartsWith("Frame rate:") && inVideoStream)
                    {
                        var parts = t.Substring(11).Trim().Split('/');
                        if (parts.Length == 2
                            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double num)
                            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double den)
                            && den > 0)
                        {
                            fps = num / den;
                        }
                    }
                    else if (t.StartsWith("Bitrate:") && inVideoStream)
                    {
                        // Берём ТОЛЬКО видео-битрейт: ниже в выводе идёт ещё и аудио-битрейт.
                        bitrate = ParseBitrateKbps(t.Substring(8).Trim());
                    }
                }

                return new VideoInfo(true, durationSec, width, height, fps, bitrate, videoCodec, audioCodec);
            }
            catch
            {
                return new VideoInfo(false, 0, 0, 0, 0, 0, "", "");
            }
        }

        /// <summary>
        /// Приводит строку битрейта из gst-discoverer к kbps.
        /// Новые версии печатают просто число в бит/с ("5564746"),
        /// старые — "4000 kbps". Поддерживаем оба варианта.
        /// </summary>
        private static int ParseBitrateKbps(string value)
        {
            string v = value.Trim();
            if (v.Length == 0) return 0;

            if (v.EndsWith("kbps", StringComparison.OrdinalIgnoreCase))
            {
                v = v.Substring(0, v.Length - 4).Trim();
            }
            else if (v.EndsWith("Mbps", StringComparison.OrdinalIgnoreCase))
            {
                v = v.Substring(0, v.Length - 4).Trim();
                return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double mb)
                    ? (int)Math.Round(mb * 1000)
                    : 0;
            }
            else if (v.EndsWith("bps", StringComparison.OrdinalIgnoreCase))
            {
                v = v.Substring(0, v.Length - 3).Trim();
            }

            if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
                return 0;

            // Меньше 1000 — это уже килобиты, иначе число пришло в битах.
            return num < 1000 ? (int)Math.Round(num) : (int)Math.Round(num / 1000.0);
        }

        // ============================================================================
        //  ПАРАМЕТРЫ КОДИРОВАНИЯ
        // ============================================================================

        private EncodingSettings GetEncodingSettings()
        {
            var (w, h) = ParseResolution(txtResolution.Text);
            return new EncodingSettings(
                w, h,
                int.Parse(txtFps.Text),
                int.Parse(txtBitrate.Text),
                int.Parse(txtAudioBitrate.Text),
                int.Parse(txtSampleRate.Text));
        }

        /// <summary>
        /// Безопасно читает и проверяет параметры кодирования.
        /// Возвращает false и человекочитаемый текст ошибки вместо исключения.
        /// </summary>
        private bool TryGetEncodingSettings(out EncodingSettings settings, out string error)
        {
            settings = default;
            error = "";

            string res = txtResolution.Text.Trim();
            var parts = res.ToLowerInvariant().Split('x');
            if (parts.Length != 2
                || !int.TryParse(parts[0].Trim(), out int w)
                || !int.TryParse(parts[1].Trim(), out int h)
                || w < 160 || h < 120 || w > 7680 || h > 4320)
            {
                error = $"Разрешение должно быть в виде ШxВ (например, 1920x1080). Сейчас: «{res}».";
                return false;
            }

            if (!int.TryParse(txtFps.Text.Trim(), out int fps) || fps < 1 || fps > 120)
            {
                error = "Кадровая частота — целое число от 1 до 120. Например: 25.";
                return false;
            }

            if (!int.TryParse(txtBitrate.Text.Trim(), out int videoKbps) || videoKbps < 100 || videoKbps > 100000)
            {
                error = "Битрейт видео — число от 100 до 100000 (Kbps). Например: 4200.";
                return false;
            }

            if (!int.TryParse(txtAudioBitrate.Text.Trim(), out int audioKbps) || audioKbps < 32 || audioKbps > 512)
            {
                error = "Битрейт аудио — число от 32 до 512 (Kbps). Например: 192.";
                return false;
            }

            if (!int.TryParse(txtSampleRate.Text.Trim(), out int sampleRate) || sampleRate < 8000 || sampleRate > 192000)
            {
                error = "Частота дискретизации — число от 8000 до 192000 (Hz). Например: 48000.";
                return false;
            }

            settings = new EncodingSettings(w, h, fps, videoKbps, audioKbps, sampleRate);
            return true;
        }

        private static (int w, int h) ParseResolution(string text)
        {
            var parts = text.Trim().ToLowerInvariant().Split('x');
            if (parts.Length != 2)
                throw new FormatException($"Разрешение должно быть в формате WxH, получено: '{text}'");
            return (int.Parse(parts[0]), int.Parse(parts[1]));
        }

        private string ExtractEmbeddedImage(string resourceName)
        {
            string outPath = Path.Combine(PrepCacheDir, resourceName);
            if (File.Exists(outPath)) return outPath;

            string fullResourceName = $"{typeof(Form1).Namespace}.{resourceName}";
            using var stream = typeof(Form1).Assembly.GetManifestResourceStream(fullResourceName);
            if (stream == null)
                throw new FileNotFoundException($"Встроенный ресурс не найден: {fullResourceName}.");

            using var fileStream = File.Create(outPath);
            stream.CopyTo(fileStream);
            return outPath;
        }

        private string BuildEncoderString(int videoKbps, int fps, int width, int height)
        {
            // Ключевой кадр раз в секунду — как у vMix (-g:v 25 при 25 к/с).
            // Для CDN это критично: плеер и упаковщик потока подключаются только
            // к ключевому кадру. При редких ключевых кадрах (по умолчанию ~10 с)
            // зритель видит «прыжок» в середину, а CDN дольше стабилизируется.
            int gop = Math.Max(1, fps);

            // Профиль H.264 в этой сборке GStreamer задать нельзя: свойства profile
            // у x264enc и nvh264enc нет, а option-string=profile=... парсер отвергает.
            // Профиль выбирает сам энкодер (NVENC — High, x264 — по параметрам качества).

            return _selectedEncoder switch
            {
                "nvh264enc" => $"nvh264enc bitrate={videoKbps} gop-size={gop} rc-mode=cbr zerolatency=true",
                "qsvh264enc" => $"qsvh264enc bitrate={videoKbps} low-latency=true",
                "amfh264enc" => $"amfh264enc bitrate={videoKbps * 1000} usage=low-latency",
                "msdkh264enc" => $"msdkh264enc bitrate={videoKbps} rate-control=cbr",
                _ => $"x264enc bitrate={videoKbps} key-int-max={gop} speed-preset=veryfast tune=zerolatency"
            };
        }

        private string BuildVideoCaps(int width, int height, int fps)
        {
            string format = _selectedEncoder == "nvh264enc" ? "NV12" : "I420";
            return $"video/x-raw,format={format},width={width},height={height},framerate={fps}/1";
        }

        // ============================================================================
        //  ПОДГОТОВКА И СТРИМИНГ
        // ============================================================================

        private void PrepareMediaMtxConfig()
        {
            string toolsDir = Path.GetDirectoryName(MediaMtxExe)!;
            string configPath = Path.Combine(toolsDir, "mediamtx.yml");
            string batPath = Path.Combine(toolsDir, "push.bat");

            string rtmpUrl = txtRtmpUrl.Text.Trim();
            string streamKey = txtStreamKey.Text.Trim();

            // RTMP: нужны и адрес, и ключ. SRT: достаточно адреса — Stream ID
            // многие приёмники (например, vMix) не требуют.
            bool hasCdn = SrtBuild
                ? !string.IsNullOrEmpty(rtmpUrl)
                  && !rtmpUrl.Equals("srt://", StringComparison.OrdinalIgnoreCase)
                : !string.IsNullOrEmpty(rtmpUrl)
                  && rtmpUrl != "rtmp://"
                  && !string.IsNullOrEmpty(streamKey);

            _hasCdn = hasCdn;
            _maskKey = hasCdn ? streamKey : "";

            var sb = new StringBuilder();
            sb.AppendLine("logLevel: info");

            // Всё слушаем ТОЛЬКО на 127.0.0.1: локальная публикация и локальное превью.
            // С адресами вида ":1935" MediaMTX слушает все интерфейсы, и Windows
            // Firewall каждый раз спрашивает разрешение для нового exe.
            // Лишние службы (HLS, WebRTC, MoQ, API) выключены — они нам не нужны,
            // но тоже открывают порты и мешают удалить папку с запущенной программой.
            sb.AppendLine("rtmp: yes");
            sb.AppendLine("rtmpAddress: 127.0.0.1:1935");
            sb.AppendLine("rtsp: yes");
            sb.AppendLine("rtspAddress: 127.0.0.1:8554");
            sb.AppendLine("rtpAddress: 127.0.0.1:8000");
            sb.AppendLine("rtcpAddress: 127.0.0.1:8001");
            sb.AppendLine("hls: no");
            sb.AppendLine("webrtc: no");
            sb.AppendLine("moq: no");
            sb.AppendLine("api: no");
            sb.AppendLine("metrics: no");
            sb.AppendLine("pprof: no");
            sb.AppendLine("playback: no");

            // SRT-сборке нужен приёмник SRT внутри MediaMTX: локально мы по-прежнему
            // публикуем по RTMP, а наружу уходим по SRT.
            //
            // ВАЖНО: адрес именно ":8890", а не "127.0.0.1:8890". На loopback
            // MediaMTX привязывает SRT-сокет к 127.0.0.1 и использует его же для
            // ИСХОДЯЩИХ подключений — пакеты уходят в петлю, приёмник их не видит,
            // и в логе появляется "connection timeout. server didn't respond".
            // Проверено: с 127.0.0.1 — таймаут, с :8890 — соединение проходит.
            if (SrtBuild)
            {
                sb.AppendLine("srt: yes");
                sb.AppendLine("srtAddress: :8890");
            }
            else
            {
                sb.AppendLine("srt: no");
            }

            sb.AppendLine("writeQueueSize: 2048");
            sb.AppendLine("paths:");

            if (hasCdn)
            {
                // Отправка на CDN встроена в MediaMTX (ключ forward, с версии 1.9):
                // он сам держит соединение с CDN и переподключается при обрывах.
                // Отдельный gst-launch с push.bat больше не нужен — а именно он
                // зависал в PREROLLING, из-за чего поток на CDN не поднимался.
                //
                // RTMP: адрес собираем в форме "путь#ключ". Если передать ключ как
                // обычный хвост URL, MediaMTX теряет часть "?auth=..." (считает её
                // query-параметром), CDN отвечает 403 publish denied.
                //
                // SRT: ключ потока — это параметр streamid в адресе приёмника.
                string destUrl;

                if (SrtBuild)
                {
                    string baseUrl = rtmpUrl.Trim().TrimEnd('/');
                    string streamId = streamKey.Trim();

                    destUrl = baseUrl;
                    if (streamId.Length > 0)
                    {
                        string separator = baseUrl.Contains('?') ? "&" : "?";
                        destUrl += $"{separator}streamid={Uri.EscapeDataString(streamId)}";
                    }
                }
                else
                {
                    string baseUrl = rtmpUrl.TrimEnd('/');
                    string key = streamKey.TrimStart('/');
                    destUrl = $"{baseUrl}#{key}";
                }

                destUrl = destUrl.Replace("\"", "%22");

                sb.AppendLine($"  {InternalStreamPath}:");
                sb.AppendLine("    source: publisher");
                sb.AppendLine("    forward:");
                sb.AppendLine($"      - dest: \"{destUrl}\"");
            }

            sb.AppendLine("  all_others:");

            File.WriteAllText(configPath, sb.ToString());

            // Старый батник больше не используется — удаляем, чтобы не путал.
            try { if (File.Exists(batPath)) File.Delete(batPath); } catch { }
        }

        private async Task PrepareAllMediaAsync()
        {
            SetStatus("Подготовка плейлиста...", StatusKind.Waiting);
            _prepCts = new CancellationTokenSource();

            _playlist.Clear();
            _totalDurationSeconds = 0;

            try
            {
                // Картинки не кодируются заранее: они собираются прямо в эфирном
                // потоке (imagefreeze), поэтому подготовка занимает доли секунды,
                // а длительность заставки задаётся точно, без кратности 5 секундам.
                //
                // В тестовом цикле заставок нет: это чистый тестовый поток,
                // и добавление intro/outro искажало длительность (таймер
                // останавливал эфир раньше, чем доигрывал финал).
                if (!IsLoopMode)
                {
                    string introImagePath = chkUseIntro.Checked && File.Exists(txtIntroJpg.Text)
                        ? txtIntroJpg.Text : ExtractEmbeddedImage("intro01.png");

                    int introSeconds = (int)numIntroDuration.Value;
                    _playlist.Add(new PlaylistSegment(SegmentKind.Image, introImagePath, introSeconds));
                    _totalDurationSeconds += introSeconds;
                }

                if (IsLoopMode)
                {
                    await BuildLoopSegmentsAsync();
                }
                else if (chkUseMainVideo.Checked && File.Exists(txtMainVideo.Text))
                {
                    string ext = Path.GetExtension(txtMainVideo.Text).ToLowerInvariant();
                    if (ext != ".mp4" && ext != ".mov" && ext != ".m4v")
                    {
                        throw new NotSupportedException(
                            $"Формат {ext} пока не поддерживается.\n\n" +
                            "Пересохраните файл в MP4 (H.264 + AAC) и выберите его снова.\n" +
                            "Например, в DaVinci Resolve, HandBrake или командой ffmpeg.");
                    }

                    var info = await AnalyzeVideoAsync(txtMainVideo.Text);
                    int mainDur = info.IsValid && info.DurationSec > 0 ? info.DurationSec : 60;
                    bool hasAudio = !string.IsNullOrWhiteSpace(info.AudioCodec);

                    _playlist.Add(new PlaylistSegment(SegmentKind.File, txtMainVideo.Text, mainDur, hasAudio));
                    _totalDurationSeconds += mainDur;

                    if (!hasAudio)
                        SetStatus("В файле нет звуковой дорожки — будет добавлена тишина", StatusKind.Waiting);
                }

                // Финальная заставка — тоже только в обычном режиме.
                if (!IsLoopMode)
                {
                    string outroImagePath = chkUseOutro.Checked && File.Exists(txtOutroJpg.Text)
                        ? txtOutroJpg.Text : ExtractEmbeddedImage("outro01.png");

                    int outroSeconds = 60;
                    _playlist.Add(new PlaylistSegment(SegmentKind.Image, outroImagePath, outroSeconds));
                    _totalDurationSeconds += outroSeconds;
                }

                // Проверяем энкодер ДО эфира: если аппаратный не работает,
                // успеваем переключиться на CPU без опоздания к времени старта.
                await EnsureEncoderWorksAsync();

                if (IsLoopMode)
                {
                    // Тестовый цикл стартует сразу, ждать назначенного времени нечего.
                    SetStatus("Запуск теста...", StatusKind.Waiting);
                    StartPreparedStream();
                    return;
                }

                SetStatus($"Ожидание старта в {dtpStartTime.Value:HH:mm:ss}", StatusKind.Waiting);
                schedulerTimer.Start();
            }
            catch (Exception ex)
            {
                // Отмена из «Аварийного стопа» — это не ошибка, окно показывать не нужно.
                if (_prepCts?.IsCancellationRequested == true)
                {
                    SetStatus("Подготовка отменена", StatusKind.Idle);
                    return;
                }

                SetStatus("Ошибка подготовки", StatusKind.Error);
                MessageBox.Show("Ошибка подготовки плейлиста:\n" + ex.Message +
                                "\n\nПодробности — кнопкой «Открыть логи» (внизу слева).",
                    "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                EnableInputs(true);
            }
        }

        /// <summary>
        /// Собирает «тело» тестового цикла: повтор файла, картинка с тоном
        /// или тестовая таблица. Длительность берётся из поля «Длительность».
        /// </summary>
        private async Task BuildLoopSegmentsAsync()
        {
            int targetSeconds = (int)numDuration.Value * 60;

            // «0» означает «работать до ручной остановки»: берём длинный отрезок,
            // а фактическую остановку сделает таймер или оператор.
            bool untilStopped = targetSeconds <= 0;
            if (untilStopped) targetSeconds = 6 * 3600;

            switch (cmbLoopSource.SelectedIndex)
            {
                case 0:   // повтор видеофайла
                {
                    string path = txtMainVideo.Text.Trim();
                    if (!File.Exists(path))
                        throw new FileNotFoundException(
                            "Для цикла нужен видеофайл: укажите его в поле «Источник».");

                    string ext = Path.GetExtension(path).ToLowerInvariant();
                    if (ext != ".mp4" && ext != ".mov" && ext != ".m4v")
                    {
                        throw new NotSupportedException(
                            $"Формат {ext} не поддерживается.\n\n" +
                            "Для цикла подойдёт MP4/MOV с видео H.264.");
                    }

                    var info = await AnalyzeVideoAsync(path);
                    int clip = info.IsValid && info.DurationSec > 0 ? info.DurationSec : 30;
                    bool hasAudio = !string.IsNullOrWhiteSpace(info.AudioCodec);

                    int repeats = (int)Math.Ceiling(targetSeconds / (double)clip);

                    // Защита от сотен веток в конвейере: лучше предупредить,
                    // чем собрать команду, которую gst-launch не переварит.
                    if (repeats > 60)
                    {
                        repeats = 60;
                        SetStatus("Ролик короткий — цикл ограничен 60 повторами", StatusKind.Waiting);
                    }
                    if (repeats < 1) repeats = 1;

                    for (int i = 0; i < repeats; i++)
                        _playlist.Add(new PlaylistSegment(SegmentKind.File, path, clip, hasAudio));

                    _totalDurationSeconds += repeats * clip;
                    break;
                }

                case 1:   // картинка + тон 1 кГц
                {
                    string path = txtMainVideo.Text.Trim();
                    if (!File.Exists(path))
                        path = ExtractEmbeddedImage("intro01.png");

                    _playlist.Add(new PlaylistSegment(SegmentKind.Image, path, targetSeconds, true, true));
                    _totalDurationSeconds += targetSeconds;
                    break;
                }

                case 2:   // картинка + внешний звуковой файл
                {
                    string image = txtMainVideo.Text.Trim();
                    if (!File.Exists(image))
                        image = ExtractEmbeddedImage("intro01.png");

                    if (_loopAudioPath.Length == 0 || !File.Exists(_loopAudioPath))
                        throw new FileNotFoundException(
                            "Не выбран звуковой файл для цикла.\n\n" +
                            "Выберите «Цикл: картинка + MP3» в списке источников и укажите файл.");

                    var audioInfo = await AnalyzeVideoAsync(_loopAudioPath);
                    int clip = audioInfo.IsValid && audioInfo.DurationSec > 0 ? audioInfo.DurationSec : 60;

                    // Каждый сегмент = картинка длиной в один проигрыш звука.
                    // Так видео и звук в сегменте всегда равны по длительности.
                    int repeats = (int)Math.Ceiling(targetSeconds / (double)clip);
                    if (repeats > 60)
                    {
                        repeats = 60;
                        SetStatus("Звук короткий — цикл ограничен 60 повторами", StatusKind.Waiting);
                    }
                    if (repeats < 1) repeats = 1;

                    for (int i = 0; i < repeats; i++)
                        _playlist.Add(new PlaylistSegment(SegmentKind.Image, image, clip, true, false, _loopAudioPath));

                    _totalDurationSeconds += repeats * clip;
                    break;
                }

                default:  // тестовая таблица SMPTE + щелчки
                {
                    _playlist.Add(new PlaylistSegment(SegmentKind.Test, "", targetSeconds));
                    _totalDurationSeconds += targetSeconds;
                    break;
                }
            }
        }

        private async void StartPreparedStream()
        {
            SetStatus("Запуск потока...", StatusKind.Main);
            try
            {
                PrepareMediaMtxConfig();
                ResetCdnStatus();
                await StartMediaMtxAsync();

                // Полоса прогресса должна идти до полной длительности эфира,
                // а не до 100 секунд (это значение у ProgressBar по умолчанию).
                progressTimer.Maximum = Math.Max(1, _totalDurationSeconds);
                progressTimer.Value = 0;
                lblCountdown.Text = TimeSpan.FromSeconds(_totalDurationSeconds).ToString(@"mm\:ss");

                _streamStartTime = DateTime.Now;

                // В тестовом цикле фиксируем момент плановой остановки.
                // Небольшой запас сверх длительности: заставок в цикле больше нет,
                // и плейлист заканчивается ровно на этом времени — пусть лучше
                // сработает штатный конец потока (код 0), а таймер будет страховкой.
                _stopBySchedule = false;
                _scheduledStopAt = (IsLoopMode && numDuration.Value > 0)
                    ? _streamStartTime.AddMinutes((double)numDuration.Value).AddSeconds(15)
                    : null;

                var s = GetEncodingSettings();
                string enc = BuildEncoderString(s.VideoKbps, s.Fps, s.Width, s.Height);
                string videoCaps = BuildVideoCaps(s.Width, s.Height, s.Fps);
                int abps = s.AudioKbps * 1000;

                // Часы на картинке — для замера задержки CDN в тестовом режиме.
                string clockPart = (IsLoopMode && chkClockOverlay.Checked)
                    ? "! clockoverlay halignment=right valignment=top shaded-background=true "
                    : "";

                // ============================================================================
                //  ОСНОВНОЙ ПАЙПЛАЙН
                //  - rtmpsink БЕЗ sync=false: синхронизация с системными часами (sync=true —
                //    значение по умолчанию) ставит поток в реальное время. С sync=false
                //    конвейер отдаёт кадры с максимальной скоростью: 5-минутный плейлист
                //    «пролетает» за 20 секунд, MediaMTX не успевает принять поток
                //    ("reader is too slow"), отправка на CDN рвётся, превью летит в ускорении.
                //  - очереди перед mux ограничены 2 секундами (было: без ограничений).
                //    С неограниченными очередями кодировщик «убегал» вперёд на минуты:
                //    поток всё равно шёл в реальном времени (его придерживает rtmpsink),
                //    но наложенные часы рисовали время КОДИРОВАНИЯ, а не отправки —
                //    и на экране отставали тем сильнее, чем дольше эфир.
                //    Ограничение создаёт обратное давление: кодировщик идёт вровень
                //    с отправкой, и часы в кадре соответствуют реальному времени.
                // ============================================================================
                const string MuxQueue = "max-size-buffers=0 max-size-bytes=0 max-size-time=2000000000";

                string args = $"-e " +
                    $"flvmux streamable=true name=mux ! rtmpsink location=\"{InternalStreamUrl}\" " +
                    // videoconvert между склейкой и энкодером обязателен: без него concat
                    // вынужден принимать формат, который требует энкодер, и ветки с другим
                    // форматом к нему не подключаются ("could not link queue to vconcat").
                    // Именно это ломало запуск на Intel QuickSync.
                    $"concat name=vconcat ! videoconvert {clockPart}! queue ! {enc} ! h264parse config-interval=-1 " +
                    $"! queue {MuxQueue} ! mux. " +
                    $"concat name=aconcat ! audiorate ! queue ! avenc_aac bitrate={abps} perfect-timestamp=true ! aacparse " +
                    $"! queue {MuxQueue} ! mux. ";

                // Превью больше НЕ живёт в этом пайплайне. Оно запускается отдельным
                // процессом и читает готовый поток из MediaMTX по RTSP, поэтому
                // любая проблема с превью физически не может остановить эфир.

                for (int i = 0; i < _playlist.Count; i++)
                {
                    var seg = _playlist[i];
                    string path = seg.Path.Replace('\\', '/');

                    // Звук картинки: тишина, тон 1 кГц или внешний файл (MP3).
                    // Внешний файл режется по кадрам (audiobuffersplit + eos-after),
                    // чтобы его длина совпадала с картинкой кадр в кадр — иначе
                    // на каждом повторе накапливался бы рассинхрон.
                    string imageAudio;
                    if (!string.IsNullOrEmpty(seg.AudioPath))
                    {
                        string audioPath = seg.AudioPath.Replace('\\', '/');
                        int audioFrames = Math.Max(1, seg.DurationSeconds * s.Fps);

                        imageAudio = $"filesrc location=\"{audioPath}\" ! decodebin " +
                                     $"! audioconvert ! audioresample " +
                                     $"! audio/x-raw,rate={s.SampleRate},channels=2,format=F32LE " +
                                     $"! audiobuffersplit output-buffer-duration=1/{s.Fps} " +
                                     $"! identity eos-after={audioFrames} ";
                    }
                    else if (seg.Tone)
                    {
                        imageAudio = $"audiotestsrc wave=sine freq=1000 samplesperbuffer={s.SampleRate} num-buffers={seg.DurationSeconds} ";
                    }
                    else
                    {
                        imageAudio = $"audiotestsrc wave=silence samplesperbuffer={s.SampleRate} num-buffers={seg.DurationSeconds} ";
                    }

                    if (seg.Kind == SegmentKind.Test)
                    {
                        // Тестовая таблица SMPTE + щелчки раз в секунду.
                        int testFrames = Math.Max(1, seg.DurationSeconds * s.Fps);

                        args += $"videotestsrc is-live=false pattern=smpte num-buffers={testFrames} " +
                                $"! video/x-raw,framerate={s.Fps}/1 " +
                                $"! videoconvert ! videoscale ! {videoCaps} " +
                                $"! queue ! vconcat. " +
                                $"audiotestsrc wave=ticks samplesperbuffer={s.SampleRate} " +
                                $"num-buffers={seg.DurationSeconds} " +
                                $"! audioconvert ! audio/x-raw,rate={s.SampleRate},channels=2,format=F32LE " +
                                $"! queue ! aconcat. ";
                    }
                    else if (seg.Kind == SegmentKind.Image)
                    {
                        // Картинка: декодируется ОДИН раз и повторяется нужное число кадров.
                        // Раньше на каждый кадр заново декодировался PNG (минута статики = 52 с).
                        int frames = Math.Max(1, seg.DurationSeconds * s.Fps);

                        args += $"filesrc location=\"{path}\" ! decodebin " +
                                $"! imagefreeze num-buffers={frames} " +
                                $"! video/x-raw,framerate={s.Fps}/1 " +
                                $"! videoconvert ! videoscale ! {videoCaps} " +
                                $"! queue ! vconcat. " +
                                imageAudio +
                                $"! audioconvert ! audio/x-raw,rate={s.SampleRate},channels=2,format=F32LE " +
                                $"! queue ! aconcat. ";
                    }
                    else
                    {
                        args += $"filesrc location=\"{path}\" ! qtdemux name=demux{i} " +
                                $"demux{i}.video_0 ! queue ! decodebin ! videoconvert ! videoscale ! videorate " +
                                $"! {videoCaps} ! queue ! vconcat. ";

                        if (seg.HasAudio)
                        {
                            args += $"demux{i}.audio_0 ! queue ! decodebin ! audioconvert ! audioresample " +
                                    $"! audio/x-raw,rate={s.SampleRate},channels=2,format=F32LE ! queue ! aconcat. ";
                        }
                        else
                        {
                            // В файле нет звука: подставляем тишину той же длины, иначе
                            // звуковая склейка отстанет от видео и в эфире съедется синхрон.
                            args += $"audiotestsrc wave=silence samplesperbuffer={s.SampleRate} " +
                                    $"num-buffers={seg.DurationSeconds} " +
                                    $"! audioconvert ! audio/x-raw,rate={s.SampleRate},channels=2,format=F32LE " +
                                    $"! queue ! aconcat. ";
                        }
                    }
                }

                StartGstProcess(args, PrepCacheDir);
                progressUpdateTimer.Start();

                // Если галочка «Показывать» снята — на чёрном поле показываем,
                // что эфир уже идёт, а не пустоту.
                if (!chkPreview.Checked)
                    SetPreviewHint("эфир идёт");

                // Превью запускаем ПОСЛЕ старта эфира и с задержкой:
                // пока MediaMTX не получил поток, RTSP-источник отвечает 404.
                _ = StartPreviewAsync();
            }
            catch (Exception ex)
            {
                SetStatus("Ошибка запуска", StatusKind.Error);
                MessageBox.Show("Ошибка запуска потока:\n" + ex.Message +
                                "\n\nПодробности — кнопкой «Открыть логи» (внизу слева).",
                    "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                StopMediaMtx();
                StopPreview();
                EnableInputs(true);
            }
        }

        private void StartGstProcess(string args, string workingDir)
        {
            string logPath = Path.Combine(workingDir, "stream_log.txt");
            logPath = PrepareLogFile(logPath);
            lock (_decodersSeen) { _decodersSeen.Clear(); }

            // Шапка лога: без неё невозможно понять, какой файл и с какими
            // параметрами играл эфир, когда что-то идёт не так.
            try
            {
                var s = GetEncodingSettings();
                var sb = new StringBuilder();
                sb.AppendLine($"=== Эфир начат: {DateTime.Now:dd.MM.yyyy HH:mm:ss} ===");
                sb.AppendLine($"Компьютер: {Environment.MachineName} ({Environment.UserName})");
                sb.AppendLine($"ОС: {RuntimeInformation.OSDescription}");
                sb.AppendLine($"CPU: {GetCpuName()} [{Environment.ProcessorCount} потоков]");
                sb.AppendLine($"ОЗУ: {GetRamText()}");
                sb.AppendLine($"GPU: {GetGpuText()}");
                sb.AppendLine($"Энкодер: {_selectedEncoder} ({GetEncoderKindText()})");
                sb.AppendLine($"Параметры: {s.Width}x{s.Height} @ {s.Fps} fps, видео {s.VideoKbps} kbps, " +
                              $"аудио {s.AudioKbps} kbps, {s.SampleRate} Hz");
                sb.AppendLine($"Длительность плейлиста: {TimeSpan.FromSeconds(_totalDurationSeconds):hh\\:mm\\:ss}");
                sb.AppendLine("Плейлист:");
                foreach (var seg in _playlist)
                {
                    sb.AppendLine($"  {seg.Kind,-5} {seg.DurationSeconds,5} с  {seg.Path}");
                }
                sb.AppendLine($"Команда: {GstLaunchExe} {args}");
                sb.AppendLine();
                File.WriteAllText(logPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }

            var psi = new ProcessStartInfo
            {
                FileName = GstLaunchExe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            ApplyGstEnvironment(psi);

            _streamProcess = new Process { StartInfo = psi };
            _streamProcess.OutputDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                AppendLog(logPath, e.Data);
                NoteDecoderFromLog(e.Data, logPath);
            };
            _streamProcess.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                AppendLog(logPath, e.Data);
                NoteDecoderFromLog(e.Data, logPath);
            };

            _streamProcess.Start();
            _streamProcess.BeginOutputReadLine();
            _streamProcess.BeginErrorReadLine();
        }

        // ============================================================================
        //  СВЕДЕНИЯ О КОМПЬЮТЕРЕ (для сравнения прогонов с разных машин)
        // ============================================================================

        private static string GetCpuName()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                if (key?.GetValue("ProcessorNameString") is string name && name.Trim().Length > 0)
                    return name.Trim();
            }
            catch { }

            return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "неизвестно";
        }

        private static string GetRamText()
        {
            try
            {
                long bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                if (bytes <= 0) return "неизвестно";
                return $"{bytes / 1024.0 / 1024.0 / 1024.0:F1} ГБ";
            }
            catch { return "неизвестно"; }
        }

        private static string GetGpuText()
        {
            var names = new List<string>();

            try
            {
                // Класс «Видеоадаптеры»: перечисляем установленные драйверы.
                using var cls = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");

                if (cls != null)
                {
                    foreach (string sub in cls.GetSubKeyNames())
                    {
                        using var dev = cls.OpenSubKey(sub);
                        if (dev?.GetValue("DriverDesc") is string desc
                            && desc.Trim().Length > 0
                            && !names.Contains(desc.Trim()))
                        {
                            names.Add(desc.Trim());
                        }
                    }
                }
            }
            catch { }

            return names.Count > 0 ? string.Join("; ", names) : "не определено";
        }

        private string GetEncoderKindText()
        {
            return _selectedEncoder switch
            {
                "nvh264enc" => "аппаратный, NVIDIA NVENC",
                "qsvh264enc" => "аппаратный, Intel QuickSync",
                "amfh264enc" => "аппаратный, AMD AMF",
                "msdkh264enc" => "аппаратный, Intel MediaSDK",
                _ => "программный, CPU"
            };
        }

        private readonly HashSet<string> _decodersSeen = new();

        /// <summary>
        /// GStreamer пишет "Got context from element 'd3d12h264dec0'" —
        /// по этой строке видно, какой декодер реально выбран.
        /// </summary>
        private void NoteDecoderFromLog(string line, string logPath)
        {
            const string marker = "Got context from element '";
            int i = line.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return;

            int start = i + marker.Length;
            int end = line.IndexOf('\'', start);
            if (end <= start) return;

            string element = line.Substring(start, end - start);
            if (!element.Contains("dec", StringComparison.OrdinalIgnoreCase)) return;

            lock (_decodersSeen)
            {
                if (!_decodersSeen.Add(element)) return;
            }

            AppendLog(logPath, $">>> ДЕКОДЕР: {element} (аппаратный)");
        }

        private void ProgressUpdateTimer_Tick(object? sender, EventArgs e)
        {
            // Плановая остановка теста: заканчиваем сами, а не по EOS плейлиста.
            if (_scheduledStopAt is DateTime stopAt && DateTime.Now >= stopAt)
            {
                _stopBySchedule = true;
                _scheduledStopAt = null;
                try { _streamProcess?.Kill(entireProcessTree: true); } catch { }
            }

            bool finished;
            try { finished = _streamProcess == null || _streamProcess.HasExited; }
            catch { finished = true; }

            if (finished)
            {
                progressUpdateTimer.Stop();

                // Код выхода нужно прочитать ДО StopMediaMtx: он обнуляет _streamProcess.
                int exitCode = -1;
                try { if (_streamProcess != null) exitCode = _streamProcess.ExitCode; } catch { }

                StopMediaMtx();
                StopPreview();
                EnableInputs(true);
                lblCountdown.Text = "00:00";
                UpdateCdnStatus("CDN: остановлен", Color.Gainsboro, Color.DimGray);

                if (exitCode == 0 || _stopBySchedule)
                {
                    progressTimer.Value = progressTimer.Maximum;
                    SetStatus(_stopBySchedule ? "Тест завершён по расписанию" : "Эфир завершен", StatusKind.Ok);
                    SetPreviewHint("эфир завершён");
                }
                else
                {
                    SetStatus($"Эфир прерван (код {exitCode}). Подробности — кнопкой «Открыть логи»", StatusKind.Error);
                    SetPreviewHint("эфир прерван");
                }

                // Итоговая строка в лог: когда эфир закончился и сколько шёл.
                try
                {
                    var runTime = DateTime.Now - _streamStartTime;
                    string ending = _stopBySchedule
                        ? "остановлен по расписанию"
                        : $"код: {exitCode}";

                    AppendLog(Path.Combine(PrepCacheDir, "stream_log.txt"),
                        $"=== Эфир завершён: {DateTime.Now:dd.MM.yyyy HH:mm:ss} | " +
                        $"длительность: {(int)runTime.TotalHours:00}:{runTime.Minutes:00}:{runTime.Seconds:00} | " +
                        $"{ending} ===");
                }
                catch { }

                // Если аппаратный декодер так и не проявился — фиксируем программное декодирование.
                int decoders;
                lock (_decodersSeen) { decoders = _decodersSeen.Count; }
                if (decoders == 0)
                    AppendLog(Path.Combine(PrepCacheDir, "stream_log.txt"),
                        ">>> ДЕКОДЕР: программный (аппаратный не использовался)");

                return;
            }

            int elapsed = (int)(DateTime.Now - _streamStartTime).TotalSeconds;
            int remaining = _totalDurationSeconds - elapsed;
            if (remaining < 0) remaining = 0;

            if (progressTimer.Maximum > 0)
                progressTimer.Value = Math.Clamp(elapsed, 0, progressTimer.Maximum);

            lblCountdown.Text = TimeSpan.FromSeconds(remaining).ToString(@"mm\:ss");

            UpdatePhaseStatus(elapsed);

            // Если CDN настроен, а MediaMTX за 20 секунд ни словом не отозвался
            // о публикации — сообщаем об этом, а не висим в «ожидании».
            if (_hasCdn && _cdnEpoch == 0 && elapsed > 20)
                UpdateCdnStatus("CDN: нет подтверждения", Color.Orange, Color.Black);
        }

        private string _lastPhaseText = "";

        /// <summary>
        /// Показывает, какой сегмент плейлиста играет сейчас:
        /// заставка, основной материал, финальная заставка.
        /// </summary>
        private void UpdatePhaseStatus(int elapsedSeconds)
        {
            if (_playlist.Count == 0) return;

            int acc = 0;
            int idx = 0;
            for (; idx < _playlist.Count; idx++)
            {
                int d = Math.Max(1, _playlist[idx].DurationSeconds);
                if (elapsedSeconds < acc + d) break;
                acc += d;
            }
            if (idx >= _playlist.Count) idx = _playlist.Count - 1;

            var seg = _playlist[idx];
            bool isFirst = idx == 0 && seg.Kind == SegmentKind.Image;
            bool isLast = idx == _playlist.Count - 1 && seg.Kind == SegmentKind.Image && _playlist.Count > 1;

            StatusKind kind;
            string text;

            switch (seg.Kind)
            {
                case SegmentKind.Test:
                    kind = StatusKind.Main;
                    text = "Тестовая таблица";
                    break;

                case SegmentKind.File:
                    kind = StatusKind.Main;
                    text = "Основной файл: " + ShortFileName(seg.Path);
                    break;

                default:
                    if (isFirst) { kind = StatusKind.Intro; text = "Заставка (intro)"; }
                    else if (isLast) { kind = StatusKind.Outro; text = "Финальная заставка (outro)"; }
                    else { kind = StatusKind.Main; text = "Цикл: картинка"; }
                    break;
            }

            if (text == _lastPhaseText) return;
            _lastPhaseText = text;
            SetStatus(text, kind);
        }

        private static string ShortFileName(string path)
        {
            string name = Path.GetFileName(path);
            return name.Length <= 40 ? name : name.Substring(0, 37) + "...";
        }

        // ============================================================================
        //  ПРЕВЬЮ: отдельный процесс GStreamer читает поток из MediaMTX по RTSP
        //  и отдаёт JPEG-кадры по TCP. Здесь — только клиент, который их принимает.
        //  Любая проблема превью не влияет на эфир: это другой процесс.
        // ============================================================================

        /// <summary>
        /// Запускает превью с задержкой и повтором: эфиру нужно несколько секунд,
        /// чтобы опубликоваться в MediaMTX (иначе RTSP вернёт 404).
        /// </summary>
        private async Task StartPreviewAsync()
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                await Task.Delay(attempt == 0 ? 2500 : 2000);

                if (_streamProcess == null || _streamProcess.HasExited)
                    return;

                StartPreview();
                await Task.Delay(1500);

                try
                {
                    if (_previewProcess != null && !_previewProcess.HasExited)
                        return;
                }
                catch
                {
                    return;
                }
            }
        }

        private void StartPreview()
        {
            StopPreview();

            if (!File.Exists(GstLaunchExe))
                return;

            if (_streamProcess == null || _streamProcess.HasExited)
                return;

            try
            {
                _previewPort = GetFreeTcpPort();
                _previewCts = new CancellationTokenSource();
                var token = _previewCts.Token;

                string logPath = Path.Combine(PrepCacheDir, "preview_log.txt");
                logPath = PrepareLogFile(logPath);

                // Превью читает поток по RTMP и ЯВНО подключает обе дорожки.
                // Вариант с RTSP не годится: у rtspsrc несколько дорожек, и
                // неподключённая звуковая вызывает ошибку "not-linked",
                // из-за которой превью умирало сразу после подключения.
                string args =
                    "-q " +
                    $"rtmpsrc location=rtmp://127.0.0.1:1935/{InternalStreamPath} " +
                    "! flvdemux name=d " +
                    "d.video ! queue ! h264parse ! avdec_h264 " +
                    "! videoconvert ! videoscale ! videorate " +
                    "! video/x-raw,width=160,height=90,framerate=5/1 " +
                    "! jpegenc quality=40 " +
                    // leaky: если клиент не успевает читать, старые кадры выбрасываются,
                    // и в окне всегда свежая картинка (иначе превью отстаёт всё сильнее
                    // и часы на экране показывают время «из прошлого»).
                    "! queue leaky=downstream max-size-buffers=1 max-size-bytes=0 max-size-time=0 " +
                    $"! tcpserversink host=127.0.0.1 port={_previewPort} sync=false async=false " +
                    "d.audio ! queue ! fakesink sync=false async=false";

                var psi = new ProcessStartInfo
                {
                    FileName = GstLaunchExe,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = GstBinDir
                };
                ApplyGstEnvironment(psi);

                _previewProcess = new Process { StartInfo = psi };
                _previewProcess.OutputDataReceived += (_, e) => { if (e.Data != null) AppendLog(logPath, e.Data); };
                _previewProcess.ErrorDataReceived += (_, e) => { if (e.Data != null) AppendLog(logPath, e.Data); };

                _previewProcess.Start();
                _previewProcess.BeginOutputReadLine();
                _previewProcess.BeginErrorReadLine();

                _ = Task.Run(() => PreviewReadLoopAsync(token), token);
            }
            catch
            {
                // Превью вспомогательное: при любой ошибке просто остаёмся без картинки.
                StopPreview();
            }
        }

        private void StopPreview()
        {
            try { _previewCts?.Cancel(); } catch { }

            try
            {
                if (_previewProcess != null && !_previewProcess.HasExited)
                    _previewProcess.Kill(entireProcessTree: true);
            }
            catch { }

            _previewProcess?.Dispose();
            _previewProcess = null;

            _previewCts?.Dispose();
            _previewCts = null;
            _previewPort = 0;

            try
            {
                if (picPreview.IsHandleCreated)
                {
                    picPreview.BeginInvoke((Action)(() =>
                    {
                        var old = picPreview.Image;
                        picPreview.Image = null;
                        old?.Dispose();
                    }));
                }
            }
            catch { }
        }

        /// <summary>
        /// Читает JPEG-поток от превью-процесса и показывает кадры в PictureBox.
        /// </summary>
        private async Task PreviewReadLoopAsync(CancellationToken token)
        {
            TcpClient? client = null;
            try
            {
                // Ждём, пока превью-процесс откроет порт (до 10 секунд).
                for (int attempt = 0; attempt < 50 && !token.IsCancellationRequested; attempt++)
                {
                    try
                    {
                        client = new TcpClient();
                        await client.ConnectAsync(IPAddress.Loopback, _previewPort, token);
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        client?.Dispose();
                        client = null;
                        await Task.Delay(200, token);
                    }
                }

                if (client == null) return;

                using var stream = client.GetStream();
                var buffer = new byte[64 * 1024];
                var framer = new JpegFramer();

                while (!token.IsCancellationRequested)
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length, token);
                    if (read == 0) break;

                    foreach (var jpeg in framer.Push(buffer.AsSpan(0, read)))
                    {
                        if (!chkPreview.Checked) continue;

                        try
                        {
                            using var ms = new MemoryStream(jpeg);
                            using var img = Image.FromStream(ms);
                            var bmp = new Bitmap(img);
                            BeginInvoke((Action)(() => ReplacePreviewImage(bmp)));
                        }
                        catch { }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                client?.Dispose();
            }
        }

        private void ReplacePreviewImage(Bitmap bmp)
        {
            if (!chkPreview.Checked || IsDisposed)
            {
                bmp.Dispose();
                return;
            }

            // Пришёл живой кадр — надпись-состояние больше не нужна.
            lblPreviewHint.Visible = false;

            var old = picPreview.Image;
            picPreview.Image = bmp;
            old?.Dispose();
        }

        /// <summary>
        /// Показывает состояние эфира на чёрном поле превью.
        /// null — скрыть надпись (когда пришла живая картинка).
        /// </summary>
        private void SetPreviewHint(string? text)
        {
            if (text == null)
            {
                lblPreviewHint.Visible = false;
                return;
            }

            lblPreviewHint.Text = text;
            lblPreviewHint.Visible = true;

            var old = picPreview.Image;
            picPreview.Image = null;
            old?.Dispose();
        }

        private bool IsStreamRunning()
        {
            try { return _streamProcess != null && !_streamProcess.HasExited; }
            catch { return false; }
        }

        private static int GetFreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        // ============================================================================
        //  MediaMTX (запуск/остановка)
        // ============================================================================

        private async Task StartMediaMtxAsync()
        {
            if (!File.Exists(MediaMtxExe))
                throw new FileNotFoundException($"Не найден mediamtx.exe: {MediaMtxExe}");

            StopMediaMtx();
            await Task.Delay(300);   // даём системе освободить порт после остановки

            if (!IsTcpPortFree(1935))
                throw new InvalidOperationException(
                    "Порт 1935 уже занят другой программой.\n\n" +
                    "Чаще всего это второй экземпляр программы или ранее запущенный MediaMTX.\n" +
                    "Закройте их и повторите запуск.");

            string logPath = Path.Combine(PrepCacheDir, "mediamtx_log.txt");
            logPath = PrepareLogFile(logPath);

            var psi = new ProcessStartInfo
            {
                FileName = MediaMtxExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(MediaMtxExe)!
            };

            _mediamtxProcess = new Process { StartInfo = psi };
            _mediamtxProcess.OutputDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                AppendLog(logPath, e.Data);
                HandleMediaMtxLine(e.Data);
            };
            _mediamtxProcess.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                AppendLog(logPath, e.Data);
                HandleMediaMtxLine(e.Data);
            };

            _mediamtxProcess.Start();
            _mediamtxProcess.BeginOutputReadLine();
            _mediamtxProcess.BeginErrorReadLine();

            // Ждём, пока MediaMTX реально откроет порт, вместо слепой паузы в 1 секунду.
            if (!await WaitForTcpPortAsync(1935, TimeSpan.FromSeconds(10)))
                throw new InvalidOperationException(
                    "MediaMTX не открыл порт 1935 за 10 секунд.\n\n" +
                    "Возможные причины:\n" +
                    "  • порт занят другой программой (второй экземпляр, старый MediaMTX);\n" +
                    "  • доступ блокирует антивирус или брандмауэр;\n" +
                    "  • не хватает прав на запуск.");
        }

        /// <summary>
        /// Проверяет, свободен ли TCP-порт. Нужна до запуска MediaMTX,
        /// чтобы отличить «порт занят» от «сервер не стартовал».
        /// </summary>
        private static bool IsTcpPortFree(int port)
        {
            TcpListener? probe = null;
            try
            {
                probe = new TcpListener(IPAddress.Any, port);
                probe.Start();
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { probe?.Stop(); } catch { }
            }
        }

        /// <summary>
        /// Асинхронно ждёт, пока на локальном порту начнёт приниматься соединение.
        /// </summary>
        private static async Task<bool> WaitForTcpPortAsync(int port, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();

            while (sw.Elapsed < timeout)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port);
                    return true;
                }
                catch
                {
                    await Task.Delay(150);
                }
            }

            return false;
        }

        // ============================================================================
        //  СТАТУС CDN (читаем ответы MediaMTX на попытки публикации)
        // ============================================================================

        private int _cdnEpoch;
        private int _cdnErrorEpoch = -1;
        private bool _hasCdn;
        private string _maskKey = "";

        /// <summary>
        /// Разбирает строки MediaMTX, относящиеся к отправке на CDN,
        /// и показывает результат в отдельной плашке.
        /// </summary>
        private void HandleMediaMtxLine(string line)
        {
            try
            {
                if (line.Contains("forwarding to", StringComparison.OrdinalIgnoreCase))
                {
                    int epoch = ++_cdnEpoch;
                    UpdateCdnStatus("CDN: подключение...", Color.LightSteelBlue, Color.MidnightBlue);
                    _ = MarkCdnOkAfterDelayAsync(epoch);
                }
                else if (line.Contains("bad result", StringComparison.OrdinalIgnoreCase))
                {
                    bool firstForThisAttempt = _cdnErrorEpoch != _cdnEpoch;
                    _cdnErrorEpoch = _cdnEpoch;

                    string reason = "ошибка публикации";
                    if (line.Contains("publish denied", StringComparison.OrdinalIgnoreCase))
                        reason = "публикация отклонена";
                    else if (line.Contains("403"))
                        reason = "отказ 403";
                    else if (line.Contains("401"))
                        reason = "отказ 401 (ключ)";
                    else if (line.Contains("timeout", StringComparison.OrdinalIgnoreCase))
                        reason = "таймаут";
                    else if (line.Contains("refused", StringComparison.OrdinalIgnoreCase))
                        reason = "соединение отклонено";

                    UpdateCdnStatus("CDN: " + reason.ToUpperInvariant(), Color.Crimson, Color.White);

                    // Отказ CDN — не мелочь: зрители ничего не видят, хотя эфир идёт.
                    // Пишем это в строку состояния, чтобы нельзя было не заметить.
                    if (firstForThisAttempt)
                        SetStatusSafe($"ВНИМАНИЕ: CDN отклонил поток ({reason}) — эфир идёт только локально",
                            StatusKind.Error);
                }
            }
            catch { }
        }

        /// <summary>
        /// Если в течение нескольких секунд после начала публикации CDN не ответил
        /// отказом, считаем, что поток принят.
        /// </summary>
        private async Task MarkCdnOkAfterDelayAsync(int epoch)
        {
            try
            {
                await Task.Delay(8000);

                // Если к этому моменту эфир уже остановлен — зелёный статус не ставим.
                bool streamAlive;
                try { streamAlive = _streamProcess != null && !_streamProcess.HasExited; }
                catch { streamAlive = false; }

                if (!streamAlive) return;
                if (epoch != _cdnEpoch || _cdnErrorEpoch == epoch) return;

                UpdateCdnStatus("CDN: публикация идёт", Color.PaleGreen, Color.DarkGreen);
            }
            catch { }
        }

        private void UpdateCdnStatus(string text, Color back, Color fore)
        {
            if (IsDisposed) return;

            try
            {
                if (lblCdnStatus.IsHandleCreated)
                {
                    lblCdnStatus.BeginInvoke((Action)(() =>
                    {
                        lblCdnStatus.Text = text;
                        lblCdnStatus.BackColor = back;
                        lblCdnStatus.ForeColor = fore;
                    }));
                }
            }
            catch { }
        }

        private void ResetCdnStatus()
        {
            _cdnEpoch = 0;
            _cdnErrorEpoch = -1;
            UpdateCdnStatus(_hasCdn ? "CDN: ожидание" : "CDN: не настроен",
                Color.Gainsboro, Color.DimGray);
        }

        private void StopMediaMtx()
        {
            if (_streamProcess != null && !_streamProcess.HasExited)
            {
                try { _streamProcess.Kill(entireProcessTree: true); } catch { }
                _streamProcess.Dispose();
                _streamProcess = null;
            }

            if (_mediamtxProcess != null && !_mediamtxProcess.HasExited)
            {
                try { _mediamtxProcess.Kill(entireProcessTree: true); } catch { }
                _mediamtxProcess.Dispose();
                _mediamtxProcess = null;
            }

            // Добиваем всё, что могло остаться от этого экземпляра программы:
            // MediaMTX и gst-launch. Проверяем путь, чтобы не тронуть чужие процессы
            // с такими же именами.
            KillOwnedProcesses("gst-launch-1.0");
            KillOwnedProcesses("mediamtx");
        }

        /// <summary>
        /// Завершает процессы с указанным именем, запущенные из папки этой программы.
        /// </summary>
        private static void KillOwnedProcesses(string processName)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            try
            {
                foreach (var p in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        string? path = null;
                        try { path = p.MainModule?.FileName; } catch { }

                        // Если путь недоступен — не трогаем: это может быть чужой процесс.
                        if (path == null) continue;
                        if (!path.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)) continue;

                        p.Kill(entireProcessTree: true);
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// При старте убирает процессы, оставшиеся от прошлого запуска
        /// (например, если программа была закрыта аварийно).
        /// </summary>
        private void KillStaleChildProcesses()
        {
            KillOwnedProcesses("gst-launch-1.0");
            KillOwnedProcesses("mediamtx");
        }

        /// <summary>
        /// Освобождает имя лога перед новым прогоном: существующий файл
        /// переименовывается по времени последней записи, чтобы прогоны
        /// не затирали друг друга. Хранятся последние <see cref="KeptLogArchives"/> копий.
        /// </summary>
        private string PrepareLogFile(string path)
        {
            const int KeptLogArchives = 20;

            try
            {
                if (!File.Exists(path))
                    return path;

                var info = new FileInfo(path);
                if (info.Length == 0)
                {
                    info.Delete();
                    return path;
                }

                string dir = info.DirectoryName!;
                string name = Path.GetFileNameWithoutExtension(info.Name);
                string ext = info.Extension;
                string stamp = info.LastWriteTime.ToString("yyyy-MM-dd_HH-mm-ss");

                // Идентификатор машины и вид сборки в имени: логи с разных
                // компьютеров и от RTMP/SRT не перезапишут друг друга при сборе.
                string kind = SrtBuild ? "srt_" : "";
                string target = Path.Combine(dir, $"{name}_{kind}{MachineId}_{stamp}{ext}");
                int n = 1;
                while (File.Exists(target))
                    target = Path.Combine(dir, $"{name}_{kind}{MachineId}_{stamp}_{n++}{ext}");

                File.Move(path, target);

                // Чистим самые старые архивы, чтобы папка не росла бесконечно.
                var archives = Directory.GetFiles(dir, $"{name}_*{ext}");
                Array.Sort(archives, StringComparer.OrdinalIgnoreCase);
                int toDelete = archives.Length - KeptLogArchives;
                for (int i = 0; i < toDelete; i++)
                {
                    try { File.Delete(archives[i]); } catch { }
                }
            }
            catch { }

            return path;
        }

        /// <summary>
        /// Открывает папку с логами в Проводнике (с выделением основного лога).
        /// </summary>
        private void OpenLogsFolder()
        {
            try
            {
                string dir = PrepCacheDir;
                Directory.CreateDirectory(dir);

                string log = Path.Combine(dir, "stream_log.txt");
                if (File.Exists(log))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{log}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось открыть папку логов:\n" + ex.Message +
                                "\n\nПуть: " + PrepCacheDir,
                    "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void AppendLog(string path, string line)
        {
            try
            {
                // Ключ потока не должен попадать в логи: их пересылают при разборе
                // проблем, а ключ — это доступ к эфиру.
                if (!string.IsNullOrEmpty(_maskKey) && line.Contains(_maskKey, StringComparison.Ordinal))
                    line = line.Replace(_maskKey, "***", StringComparison.Ordinal);

                lock (this) { File.AppendAllText(path, line + Environment.NewLine); }
            }
            catch { }
        }

        // ============================================================================
        //  РЕЖИМ РАБОТЫ: эфир по расписанию / тестовый цикл
        // ============================================================================

        private bool IsLoopMode => cmbMode.SelectedIndex == 1;

        /// <summary>
        /// В режиме цикла поле «Источник» — это картинка, а не видео.
        /// </summary>
        private bool LoopSourceIsImage =>
            IsLoopMode && (cmbLoopSource.SelectedIndex == 1 || cmbLoopSource.SelectedIndex == 2);

        /// <summary>
        /// Показывает поля, относящиеся к выбранному режиму.
        /// </summary>
        private void ApplyMode()
        {
            bool loop = IsLoopMode;

            lblStartTime.Visible = !loop;
            dtpStartTime.Visible = !loop;

            lblDuration.Visible = loop;
            numDuration.Visible = loop;
            lblMinutes.Visible = loop;
            chkClockOverlay.Visible = loop;
            cmbLoopSource.Visible = loop;

            btnStart.Text = loop ? "ЗАПУСТИТЬ ТЕСТ" : "ПУСК";

            // В тестовом цикле заставки не используются — гасим их,
            // чтобы не было сомнений: тест идёт от начала до конца.
            chkUseIntro.Enabled = !loop;
            chkUseOutro.Enabled = !loop;

            if (loop)
            {
                txtIntroJpg.Enabled = false;
                btnBrowseIntro.Enabled = false;
                numIntroDuration.Enabled = false;
                txtOutroJpg.Enabled = false;
                btnBrowseOutro.Enabled = false;

                _tip.SetToolTip(chkUseIntro, "В тестовом цикле заставки не используются");
                _tip.SetToolTip(chkUseOutro, "В тестовом цикле заставки не используются");

                chkUseMainVideo.Text = "Источник:";
                UpdateLoopSourceTooltip();
                SetStatus("Готов (тестовый цикл)", StatusKind.Idle);
            }
            else
            {
                _tip.SetToolTip(chkUseIntro, "Показывать заставку перед основным видео");
                _tip.SetToolTip(chkUseOutro, "Показывать финальную заставку");

                chkUseIntro_CheckedChanged(null, EventArgs.Empty);
                chkUseOutro_CheckedChanged(null, EventArgs.Empty);

                chkUseMainVideo.Text = "Main Video:";
                SetStatus("Готов", StatusKind.Idle);
            }
        }

        /// <summary>
        /// Выбор источника цикла. Для «картинка + MP3» сразу предлагает выбрать звук.
        /// </summary>
        private void cmbLoopSource_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (!IsLoopMode || _suppressLoopSourceChange) return;

            if (cmbLoopSource.SelectedIndex == 2)
            {
                // Всегда спрашиваем файл: так его можно сменить в любой момент.
                if (!AskLoopAudioFile())
                {
                    // Отмена — возвращаем прежний пункт, чтобы не остаться без звука.
                    _suppressLoopSourceChange = true;
                    cmbLoopSource.SelectedIndex = Math.Max(0, _lastLoopSourceIndex);
                    _suppressLoopSourceChange = false;
                    UpdateLoopSourceTooltip();
                    return;
                }
            }

            _lastLoopSourceIndex = cmbLoopSource.SelectedIndex;
            UpdateLoopSourceTooltip();
        }

        /// <summary>
        /// Спрашивает путь к звуковому файлу для цикла. true — файл выбран.
        /// </summary>
        private bool AskLoopAudioFile()
        {
            using var dlg = new OpenFileDialog
            {
                Filter = "Аудио|*.mp3;*.m4a;*.aac;*.wav|Все файлы|*.*",
                CheckFileExists = true,
                Multiselect = false,
                Title = "Звук для цикла"
            };

            if (_loopAudioPath.Length > 0 && File.Exists(_loopAudioPath))
                dlg.FileName = _loopAudioPath;

            if (dlg.ShowDialog(this) != DialogResult.OK)
                return false;

            _loopAudioPath = dlg.FileName;
            UpdateLoopSourceCaption();
            return true;
        }

        /// <summary>
        /// Показывает в пункте списка имя выбранного звукового файла.
        /// </summary>
        private void UpdateLoopSourceCaption()
        {
            if (cmbLoopSource.Items.Count < 3) return;

            string caption = _loopAudioPath.Length > 0
                ? $"Цикл: картинка + MP3 ({Path.GetFileName(_loopAudioPath)})"
                : "Цикл: картинка + MP3";

            // Смена текста пункта не должна выглядеть как смена выбора.
            _suppressLoopSourceChange = true;
            int idx = cmbLoopSource.SelectedIndex;
            cmbLoopSource.Items[2] = caption;
            cmbLoopSource.SelectedIndex = idx;
            _suppressLoopSourceChange = false;
        }

        /// <summary>
        /// Подсказка к полю «Источник» для текущего варианта цикла.
        /// </summary>
        private void UpdateLoopSourceTooltip()
        {
            // Кнопка смены звука нужна только для варианта «картинка + MP3».
            if (btnLoopAudio != null)
                btnLoopAudio.Visible = IsLoopMode && cmbLoopSource.SelectedIndex == 2;

            switch (cmbLoopSource.SelectedIndex)
            {
                case 0:
                    _tip.SetToolTip(txtMainVideo, "Видеофайл, который будет повторяться всё время теста");
                    break;
                case 1:
                    _tip.SetToolTip(txtMainVideo, "Картинка (JPG/PNG). Звук — тон 1 кГц");
                    break;
                case 2:
                    _tip.SetToolTip(txtMainVideo, "Картинка (JPG/PNG). Звук — выбранный MP3-файл");
                    _tip.SetToolTip(cmbLoopSource, _loopAudioPath.Length > 0
                        ? "Звук: " + _loopAudioPath
                        : "Звук не выбран — нажмите, чтобы выбрать");
                    break;
                default:
                    _tip.SetToolTip(txtMainVideo, "Не используется: идёт тестовая таблица SMPTE + щелчки");
                    break;
            }
        }

        private void EnableInputs(bool on)
        {
            gbEncoding.Enabled = on;
            gbSources.Enabled = on;
            gbBroadcast.Enabled = on;
            gbProfile.Enabled = on;
            btnStart.Enabled = on;
            btnEmergencyStop.Enabled = !on;
        }

        // ============================================================================
        //  ОБРАБОТЧИКИ СОБЫТИЙ
        // ============================================================================

        private async void btnStart_Click(object? sender, EventArgs e)
        {
            // В тестовом режиме эфир начинается сразу, проверять время старта не нужно.
            if (!IsLoopMode && dtpStartTime.Value <= DateTime.Now)
            {
                MessageBox.Show("Время старта должно быть в будущем!", "Ошибка",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!TryGetEncodingSettings(out _, out string settingsError))
            {
                MessageBox.Show(settingsError, "Проверьте параметры кодирования",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Проверка адреса CDN до старта: опечатка в адресе или пустой ключ
            // иначе выяснятся уже во время эфира, когда зрители ничего не видят.
            if (!TryValidateBroadcast(out bool cdnConfigured, out string broadcastError))
            {
                MessageBox.Show(broadcastError, "Проверьте настройки трансляции",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtRtmpUrl.Focus();
                return;
            }

            if (!cdnConfigured)
            {
                var answer = MessageBox.Show(
                    "Адрес CDN или ключ потока не заполнены.\n\n" +
                    "Эфир пойдёт ТОЛЬКО локально (в MediaMTX), зрители на CDN его не увидят.\n\n" +
                    "Продолжить?",
                    "CDN не настроен", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (answer != DialogResult.Yes)
                    return;
            }

            EnableInputs(false);
            SetPreviewHint("ожидание эфира");
            await PrepareAllMediaAsync();
        }

        /// <summary>
        /// Проверяет адрес CDN и ключ потока до начала эфира.
        /// Возвращает false и текст ошибки, если адрес заведомо неверный.
        /// cdnConfigured = false означает «CDN не настроен», это не ошибка.
        /// </summary>
        private bool TryValidateBroadcast(out bool cdnConfigured, out string error)
        {
            cdnConfigured = false;
            error = "";

            // В SRT-сборке набор полей другой — проверяем отдельно.
            if (SrtBuild)
                return TryValidateSrtBroadcast(out cdnConfigured, out error);

            string url = txtRtmpUrl.Text.Trim();
            string key = txtStreamKey.Text.Trim();

            bool urlEmpty = string.IsNullOrEmpty(url) || url == "rtmp://" || url == "rtmps://";

            if (urlEmpty && key.Length == 0)
                return true;   // локальный режим, подтверждение спросим отдельно

            if (urlEmpty)
            {
                error = "Указан ключ потока, но не указан адрес CDN.\n\n" +
                        "Пример адреса: rtmp://a.r.cdnvideo.net/livemaster/";
                return false;
            }

            if (!url.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase))
            {
                error = "Адрес CDN должен начинаться с rtmp:// или rtmps://\n\n" +
                        $"Сейчас указано: {url}";
                return false;
            }

            if (url.IndexOf(' ') >= 0)
            {
                error = "В адресе CDN есть пробелы — скопируйте адрес заново из личного кабинета.";
                return false;
            }

            // После схемы должен быть хотя бы хост
            string afterScheme = url.Substring(url.IndexOf("://", StringComparison.Ordinal) + 3);
            string host = afterScheme.Split('/')[0];
            if (host.Length == 0)
            {
                error = "В адресе CDN не указан сервер.\n\n" +
                        "Пример: rtmp://a.r.cdnvideo.net/livemaster/";
                return false;
            }

            if (key.Length == 0)
            {
                error = "Указан адрес CDN, но не указан ключ потока.\n\n" +
                        "Без ключа провайдер не пустит поток в эфир.";
                return false;
            }

            if (key.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '"' }) >= 0)
            {
                error = "В ключе потока есть пробелы, переносы строк или кавычки.\n\n" +
                        "Скопируйте ключ заново — он должен быть одной строкой без пробелов.";
                return false;
            }

            cdnConfigured = true;
            return true;
        }

        /// <summary>
        /// Проверка адреса для SRT-сборки: схема, хост, обязательный порт,
        /// необязательный Stream ID без служебных символов.
        /// </summary>
        private bool TryValidateSrtBroadcast(out bool cdnConfigured, out string error)
        {
            cdnConfigured = false;
            error = "";

            string url = txtRtmpUrl.Text.Trim();
            string streamId = txtStreamKey.Text.Trim();

            bool urlEmpty = string.IsNullOrEmpty(url) || url.Equals("srt://", StringComparison.OrdinalIgnoreCase);

            if (urlEmpty && streamId.Length == 0)
                return true;   // локальный режим

            if (urlEmpty)
            {
                error = "Указан Stream ID, но не указан адрес SRT.\n\n" +
                        "Пример: srt://192.168.1.50:9000";
                return false;
            }

            if (!url.StartsWith("srt://", StringComparison.OrdinalIgnoreCase))
            {
                error = "Адрес SRT должен начинаться с srt://\n\n" +
                        $"Сейчас указано: {url}";
                return false;
            }

            if (url.IndexOf(' ') >= 0)
            {
                error = "В адресе SRT есть пробелы — уберите их.";
                return false;
            }

            string afterScheme = url.Substring("srt://".Length);
            string hostPort = afterScheme.Split('/')[0].Split('?')[0];

            if (hostPort.Length == 0)
            {
                error = "В адресе SRT не указан сервер.\n\n" +
                        "Пример: srt://192.168.1.50:9000";
                return false;
            }

            int colon = hostPort.LastIndexOf(':');
            if (colon < 0
                || colon == hostPort.Length - 1
                || !int.TryParse(hostPort.Substring(colon + 1), out int port)
                || port < 1 || port > 65535)
            {
                error = "В адресе SRT должен быть порт приёмника.\n\n" +
                        "Пример: srt://192.168.1.50:9000";
                return false;
            }

            if (streamId.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '"', '&', '?' }) >= 0)
            {
                error = "В Stream ID есть недопустимые символы: пробелы, кавычки, «&» или «?».\n\n" +
                        "Stream ID должен быть одной строкой без служебных символов.";
                return false;
            }

            cdnConfigured = true;
            return true;
        }

        private void schedulerTimer_Tick(object? sender, EventArgs e)
        {
            if (DateTime.Now >= dtpStartTime.Value)
            {
                schedulerTimer.Stop();
                StartPreparedStream();
            }
            else
            {
                var span = dtpStartTime.Value - DateTime.Now;
                lblCountdown.Text = span.ToString(@"mm\:ss");
            }
        }

        private void btnEmergencyStop_Click(object? sender, EventArgs e)
        {
            try { _prepCts?.Cancel(); } catch { }

            schedulerTimer.Stop();
            progressUpdateTimer.Stop();
            StopMediaMtx();
            StopPreview();

            progressTimer.Value = 0;
            lblCountdown.Text = "00:00";
            SetStatus("Аварийный стоп", StatusKind.Error);
            SetPreviewHint("остановлено");
            UpdateCdnStatus("CDN: остановлен", Color.Gainsboro, Color.DimGray);
            EnableInputs(true);
        }

        private void btnExitProgram_Click(object? sender, EventArgs e) => Close();

        private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
        {
            try { _prepCts?.Cancel(); } catch { }
            try { schedulerTimer?.Stop(); } catch { }
            try { progressUpdateTimer?.Stop(); } catch { }
            try { StopMediaMtx(); } catch { }
            try { StopPreview(); } catch { }

            if (!string.IsNullOrEmpty(_currentProfileName) && _profiles.ContainsKey(_currentProfileName))
            {
                _profiles[_currentProfileName] = GetCurrentProfileValues();
                SaveProfilesToDisk();
            }
        }

        private void chkPreview_CheckedChanged(object? sender, EventArgs e)
        {
            // Галочка управляет только показом картинки: сама рамка видна всегда
            // и без картинки показывает состояние эфира.
            // При включении надпись не убираем — она исчезнет сама, когда придёт кадр.
            if (!chkPreview.Checked)
                SetPreviewHint(IsStreamRunning() ? "эфир идёт" : "нет эфира");
        }

        private void chkUseIntro_CheckedChanged(object? sender, EventArgs e)
        {
            // Снятая галочка = строка неактивна целиком: и поле, и кнопка выбора,
            // и длительность. Так нет противоречия «поле активно, но не используется».
            bool on = chkUseIntro.Checked;
            txtIntroJpg.Enabled = on;
            btnBrowseIntro.Enabled = on;
            numIntroDuration.Enabled = on;

            // Предупреждение — только когда галочку снял пользователь.
            // Программные вызовы (загрузка настроек, смена режима) приходят
            // с sender = null, и окно на старте появляться не должно.
            if (!on && sender != null) ShowIntroWarning();
        }

        private void chkUseOutro_CheckedChanged(object? sender, EventArgs e)
        {
            bool on = chkUseOutro.Checked;
            txtOutroJpg.Enabled = on;
            btnBrowseOutro.Enabled = on;
        }

        private void chkUseMainVideo_CheckedChanged(object? sender, EventArgs e)
        {
            bool on = chkUseMainVideo.Checked;
            txtMainVideo.Enabled = on;
            btnBrowseMain.Enabled = on;
        }

        private void ShowIntroWarning()
        {
            var dlg = new Form
            {
                Text = "Предупреждение",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(440, 150),
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                BackColor = Color.WhiteSmoke
            };

            var lbl = new Label
            {
                Text = "Без начальной заставки воспроизведение видеофайла возможно " +
                       "некорректно для зрителей. Будет использована встроенная заставка.",
                Location = new Point(15, 15),
                Size = new Size(410, 65),
                TextAlign = ContentAlignment.MiddleLeft
            };

            var btn = new Button
            {
                Text = "Закрыть окно",
                Location = new Point(310, 105),
                Size = new Size(115, 30)
            };
            btn.Click += (_, _) => dlg.Close();

            var timer = new System.Windows.Forms.Timer { Interval = 15000 };
            timer.Tick += (_, _) => { timer.Stop(); dlg.Close(); };
            timer.Start();
            dlg.FormClosed += (_, _) => timer.Dispose();

            dlg.Controls.Add(lbl);
            dlg.Controls.Add(btn);
            dlg.ShowDialog(this);
        }
    }
}
