using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using OneSTechLogExporter.Core.Models;
using OneSTechLogExporter.Core.Parsers;
using OneSTechLogExporter.Core.Serialization;
using OneSTechLogExporter.Core.Services;

namespace OneSTechLogExporter.Gui;

/// <summary>
/// Главное окно графического WPF-приложения отладки логов 1С и управления подключениями Elastic/Kibana.
/// </summary>
public partial class MainWindow : Window
{
    [DllImport("kernel32.dll", EntryPoint = "SetProcessWorkingSetSize", ExactSpelling = true, CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    /// <summary>
    /// Полный сброс неиспользуемой оперативной памяти и освобождение Working Set процесса в ОС Windows.
    /// </summary>
    public static void DeepGarbageCollection()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        try
        {
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
        }
        catch { }
    }
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private readonly List<TechLogDoc> _techLogDocs = [];
    private readonly List<EventLogDoc> _eventLogDocs = [];
    private ICollectionView? _tgCollectionView;
    private ICollectionView? _lgCollectionView;
    private CancellationTokenSource? _parsingCts;
    private long _lastProgressUpdateTick;
    private long _lastPopupCloseTick;

    private void MenuPopup_Closed(object sender, EventArgs e)
    {
        _lastPopupCloseTick = Stopwatch.GetTimestamp();
    }

    private void MenuToggleButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ToggleButton tb)
        {
            var elapsedMs = (Stopwatch.GetTimestamp() - _lastPopupCloseTick) * 1000.0 / Stopwatch.Frequency;
            if (tb.IsChecked == true || elapsedMs < 350)
            {
                tb.IsChecked = false;
                e.Handled = true;
            }
        }
    }

    private void MenuToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb)
        {
            var elapsedMs = (Stopwatch.GetTimestamp() - _lastPopupCloseTick) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs < 350 && tb.IsChecked == true)
            {
                tb.IsChecked = false;
            }
        }
    }

    private bool _tgHasTimeFrom;
    private bool _tgHasTimeTo;
    private TimeSpan _tgTimeFrom;
    private TimeSpan _tgTimeTo;
    private DateTime _tgExactFrom;
    private DateTime _tgExactTo;

    private bool _lgHasTimeFrom;
    private bool _lgHasTimeTo;
    private TimeSpan _lgTimeFrom;
    private TimeSpan _lgTimeTo;
    private DateTime _lgExactFrom;
    private DateTime _lgExactTo;

    // Активные колонки и направления сортировки (Direct In-Memory High-Speed Sorting)
    private readonly List<(string SortMemberPath, ListSortDirection Direction)> _activeTgSorts = [("Date", ListSortDirection.Descending)];
    private readonly List<(string SortMemberPath, ListSortDirection Direction)> _activeLgSorts = [("Date", ListSortDirection.Descending)];

    private readonly ObservableCollection<FilterCheckItem> _tgExcludeEventItems = [];
    private readonly ObservableCollection<FilterCheckItem> _lgExcludeEventItems = [];

    // Коллекции уникальных значений полей ТЖ
    private readonly ObservableCollection<FieldValueItem> _tgUserItems = [];
    private readonly ObservableCollection<FieldValueItem> _tgAppItems = [];
    private readonly ObservableCollection<FieldValueItem> _tgPidItems = [];
    private readonly ObservableCollection<FieldValueItem> _tgSpidItems = [];
    private readonly ObservableCollection<FieldValueItem> _tgThreadItems = [];

    // Коллекции уникальных значений полей ЖР
    private readonly ObservableCollection<FieldValueItem> _lgUserItems = [];
    private readonly ObservableCollection<FieldValueItem> _lgAppItems = [];
    private readonly ObservableCollection<FieldValueItem> _lgMetaItems = [];
    private readonly ObservableCollection<FieldValueItem> _lgEventFieldItems = [];

    public MainWindow()
    {
        InitializeComponent();
        WindowState = WindowState.Maximized;
        TrySetWindowIcon();
        InitAppBuildDate();

        // Привязка списков отбора по полям
        ListTgUsers.ItemsSource = _tgUserItems;
        ListTgApps.ItemsSource = _tgAppItems;
        ListTgPids.ItemsSource = _tgPidItems;
        ListTgSpids.ItemsSource = _tgSpidItems;
        ListTgThreads.ItemsSource = _tgThreadItems;

        ListLgUsers.ItemsSource = _lgUserItems;
        ListLgApps.ItemsSource = _lgAppItems;
        ListLgMetas.ItemsSource = _lgMetaItems;
        ListLgEvents.ItemsSource = _lgEventFieldItems;

        PopulateTgEventFilterItems();
        PopulateTgExcludeFilterItems();
        PopulateLgEventFilterItems();
        PopulateLgExcludeFilterItems();
        LoadConfigIfPresent();
        LoadGuiState();
        UpdateTgFilterChips();
        UpdateLgFilterChips();

        Closing += (_, _) => SaveGuiState();
    }

    /// <summary>
    /// Инициализация даты редакции/сборки приложения в правом нижнем углу.
    /// </summary>
    private void InitAppBuildDate()
    {
        try
        {
            var asm = typeof(MainWindow).Assembly;
            var fileLocation = asm.Location;
            DateTime buildDate;
            if (!string.IsNullOrEmpty(fileLocation) && File.Exists(fileLocation))
            {
                buildDate = File.GetLastWriteTime(fileLocation);
            }
            else
            {
                var entryPath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(entryPath) && File.Exists(entryPath))
                {
                    buildDate = File.GetLastWriteTime(entryPath);
                }
                else
                {
                    buildDate = DateTime.Now;
                }
            }
            TxtAppBuildDate.Text = $"Редакция: {buildDate:dd.MM.yyyy HH:mm}";
        }
        catch
        {
            TxtAppBuildDate.Text = $"Редакция: {DateTime.Now:dd.MM.yyyy}";
        }
    }

    /// <summary>
    /// Отображение нижней полосы прогресса при выполнении длительных операций парсинга.
    /// </summary>
    private void ShowProgress(string details = "")
    {
        _lastProgressUpdateTick = 0;
        PanelProgress.Visibility = Visibility.Visible;
        ProgressBarParsing.Value = 0;
        TxtProgressPercent.Text = "0%";
        TxtProgressDetails.Text = details;
    }

    /// <summary>
    /// Потокобезопасное плавное обновление процента и деталей прогресс-бара с троттлингом.
    /// </summary>
    private void UpdateProgress(double percent, string details = "")
    {
        var now = Stopwatch.GetTimestamp();
        // Обновляем UI с частотой ~20 fps, чтобы поток отрисовки не зависал
        if (percent < 100.0 && percent > 0.0 && (now - _lastProgressUpdateTick) < (Stopwatch.Frequency / 20))
            return;

        _lastProgressUpdateTick = now;

        Dispatcher.InvokeAsync(() =>
        {
            var clamped = Math.Clamp(percent, 0.0, 100.0);
            ProgressBarParsing.Value = clamped;
            TxtProgressPercent.Text = $"{clamped:F0}%";
            if (!string.IsNullOrEmpty(details))
            {
                TxtProgressDetails.Text = details;
            }
        }, System.Windows.Threading.DispatcherPriority.Render);
    }

    /// <summary>
    /// Немедленная отмена текущего парсинга по запросу пользователя.
    /// </summary>
    private void BtnCancelParsing_Click(object sender, RoutedEventArgs e)
    {
        if (_parsingCts != null && !_parsingCts.IsCancellationRequested)
        {
            _parsingCts.Cancel();
            TxtStatus.Text = "⏳ Отправлен сигнал остановки... Завершение текущих потоков чтения.";
        }
    }

    /// <summary>
    /// Скрытие полосы прогресса по завершении или прерывании операции.
    /// </summary>
    private void HideProgress()
    {
        PanelProgress.Visibility = Visibility.Collapsed;
        ProgressBarParsing.Value = 0;
        TxtProgressPercent.Text = "0%";
        TxtProgressDetails.Text = string.Empty;
    }

    /// <summary>
    /// Безопасная попытка установки иконки окна без выброса исключений при ее отсутствии.
    /// </summary>
    private void TrySetWindowIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "icon.ico");
            if (File.Exists(iconPath))
            {
                Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(iconPath));
            }
        }
        catch
        {
            // Безопасное проглатывание ошибок загрузки визуальной иконки
        }
    }

    /// <summary>
    /// Автоматическая загрузка параметров из файла appsettings.json при наличии.
    /// </summary>
    private void LoadConfigIfPresent()
    {
        try
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (doc.RootElement.TryGetProperty("Exporter", out var exporter))
                {
                    if (exporter.TryGetProperty("Elastic", out var elastic))
                    {
                        if (elastic.TryGetProperty("ServerUrl", out var url)) TxtElasticUrl.Text = url.GetString() ?? "http://localhost:9200";
                        if (elastic.TryGetProperty("Username", out var user)) TxtElasticUser.Text = user.GetString() ?? string.Empty;
                        if (elastic.TryGetProperty("Password", out var pass)) TxtElasticPass.Password = pass.GetString() ?? string.Empty;
                        if (elastic.TryGetProperty("ApiKey", out var apiKey)) TxtElasticApiKey.Text = apiKey.GetString() ?? string.Empty;
                        if (elastic.TryGetProperty("Enabled", out var enabled)) ChkElasticEnabled.IsChecked = enabled.GetBoolean();
                        if (elastic.TryGetProperty("EventLogIndexPrefix", out var evPrefix)) TxtEventLogPrefix.Text = evPrefix.GetString() ?? "events";
                        if (elastic.TryGetProperty("TechLogIndexPrefix", out var tgPrefix)) TxtTechLogPrefix.Text = tgPrefix.GetString() ?? "techlog";
                    }

                    if (exporter.TryGetProperty("Kibana", out var kibana))
                    {
                        if (kibana.TryGetProperty("ServerUrl", out var kUrl)) TxtKibanaUrl.Text = kUrl.GetString() ?? "http://localhost:5601";
                    }

                    if (exporter.TryGetProperty("TechLog", out var tg))
                    {
                        if (tg.TryGetProperty("DirectoryPath", out var tgPath))
                        {
                            var pathStr = tgPath.GetString();
                            if (!string.IsNullOrWhiteSpace(pathStr)) TxtTgPath.Text = pathStr;
                        }
                    }

                    if (exporter.TryGetProperty("EventLog", out var evLog))
                    {
                        if (evLog.TryGetProperty("DirectoryPath", out var evPath))
                        {
                            var pathStr = evPath.GetString();
                            if (!string.IsNullOrWhiteSpace(pathStr)) TxtLgPath.Text = pathStr;
                        }
                    }

                    if (exporter.TryGetProperty("FileDump", out var dump))
                    {
                        if (dump.TryGetProperty("DirectoryPath", out var dPath)) TxtDumpDirectory.Text = dPath.GetString() ?? "parsed_logs";
                    }
                }
            }
        }
        catch
        {
            // Ошибки проглатываются для обеспечения устойчивости GUI при старте
        }
    }

    /// <summary>
    /// Формирование объекта настроек ElasticSettings на основе введенных в GUI параметров.
    /// </summary>
    private ElasticSettings BuildElasticSettingsFromUi()
    {
        return new ElasticSettings
        {
            Enabled = ChkElasticEnabled.IsChecked ?? true,
            ServerUrl = string.IsNullOrWhiteSpace(TxtElasticUrl.Text) ? "http://localhost:9200" : TxtElasticUrl.Text.Trim(),
            Username = TxtElasticUser.Text.Trim(),
            Password = TxtElasticPass.Password.Trim(),
            ApiKey = TxtElasticApiKey.Text.Trim(),
            EventLogIndexPrefix = string.IsNullOrWhiteSpace(TxtEventLogPrefix.Text) ? "events" : TxtEventLogPrefix.Text.Trim(),
            TechLogIndexPrefix = string.IsNullOrWhiteSpace(TxtTechLogPrefix.Text) ? "techlog" : TxtTechLogPrefix.Text.Trim(),
            BulkBatchSize = 1000
        };
    }

    /// <summary>
    /// Выбор каталога Технологического Журнала.
    /// </summary>
    private void BtnBrowseTg_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите каталог Технологического Журнала (TGLogs)"
        };
        if (dialog.ShowDialog() == true)
        {
            TxtTgPath.Text = dialog.FolderName;
        }
    }

    /// <summary>
    /// Выбор каталога Журнала Регистрации.
    /// </summary>
    private void BtnBrowseLg_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите каталог Журнала Регистрации (LGLogs)"
        };
        if (dialog.ShowDialog() == true)
        {
            TxtLgPath.Text = dialog.FolderName;
        }
    }

    /// <summary>
    /// Высокопроизводительный потоковый парсинг Технологического Журнала с возможностью мгновенной отмены.
    /// </summary>
    private async void BtnParseTg_Click(object sender, RoutedEventArgs e)
    {
        var path = TxtTgPath.Text.Trim();
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            MessageBox.Show($"Путь к ТЖ не найден: {path}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _parsingCts = new CancellationTokenSource();
        var ct = _parsingCts.Token;

        BtnParseTg.IsEnabled = false;
        BtnCancelTg.Visibility = Visibility.Visible;
        TxtStatus.Text = "Поиск и потоковый разбор файлов Технологического Журнала...";
        _techLogDocs.Clear();
        _techLogDocs.TrimExcess();
        DeepGarbageCollection();
        GridTg.ItemsSource = null;
        ShowProgress("Подготовка файлов ТЖ...");

        try
        {
            var logItems = LogDiscovery.FindTechLogFiles(path).ToList();
            if (logItems.Count == 0)
            {
                MessageBox.Show($"Файлы Технологического Журнала (*.log) не найдены по пути: {path}", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Information);
                TxtStatus.Text = "Файлы ТЖ не найдены.";
                return;
            }

            var maxDocs = GetPreviewLimit(CmbTgLimit);
            var totalBytes = logItems.Sum(item =>
            {
                try { return new FileInfo(item.FilePath).Length; } catch { return 0L; }
            });

            var maxDegree = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
            var channel = Channel.CreateBounded<TechLogDoc>(new BoundedChannelOptions(10_000)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true
            });

            long processedBytes = 0;
            int completedFiles = 0;

            var producerTask = Task.Run(async () =>
            {
                try
                {
                    await Parallel.ForEachAsync(logItems, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = maxDegree,
                        CancellationToken = ct
                    }, async (item, token) =>
                    {
                        long fileLength = 0;
                        try { fileLength = new FileInfo(item.FilePath).Length; } catch { }

                        await foreach (var doc in TechLogParser.ParseFileAsync(item.FilePath, item.ProcessName, item.ProcessId, ct: token))
                        {
                            await channel.Writer.WriteAsync(doc, token);
                        }

                        var currentDone = Interlocked.Increment(ref completedFiles);
                        var currentBytes = Interlocked.Add(ref processedBytes, fileLength);
                        var pct = totalBytes > 0 ? (double)currentBytes / totalBytes * 100.0 : 0.0;
                        UpdateProgress(pct, $"Файлов {currentDone}/{logItems.Count}");
                    });
                }
                catch (OperationCanceledException) { }
                finally
                {
                    channel.Writer.Complete();
                }
            }, ct);

            var batch = new List<TechLogDoc>(500);
            try
            {
                while (await channel.Reader.WaitToReadAsync(ct))
                {
                    while (channel.Reader.TryRead(out var doc))
                    {
                        batch.Add(doc);
                        if (batch.Count >= 500)
                        {
                            _techLogDocs.AddRange(batch);
                            batch.Clear();
                            UpdateTgCountStatus();
                            await Task.Yield(); // Освобождаем UI-поток для плавной отрисовки и отклика кнопок
                            if (_techLogDocs.Count >= maxDocs) break;
                        }
                    }
                    if (batch.Count > 0)
                    {
                        _techLogDocs.AddRange(batch);
                        batch.Clear();
                        UpdateTgCountStatus();
                    }
                    if (_techLogDocs.Count >= maxDocs) break;
                }
            }
            catch (OperationCanceledException) { }

            try { await producerTask; } catch (OperationCanceledException) { }

            // Сортировка по активным колонкам (Direct In-Memory Sort)
            if (_activeTgSorts.Count > 0)
            {
                SortTechLogDocs(_techLogDocs, _activeTgSorts);
            }
            else
            {
                ApplyTgSortPreset();
            }

            PopulateTgEventFilterItems();
            PopulateTgExcludeFilterItems();
            PopulateTgFieldValues();
            _tgCollectionView = CollectionViewSource.GetDefaultView(_techLogDocs);
            _tgCollectionView.Filter = FilterTgDoc;

            foreach (var col in GridTg.Columns)
            {
                var match = _activeTgSorts.FirstOrDefault(s => string.Equals(s.SortMemberPath, col.SortMemberPath, StringComparison.OrdinalIgnoreCase));
                col.SortDirection = match != default ? match.Direction : null;
            }

            GridTg.ItemsSource = _tgCollectionView;
            UpdateTgFilterChips();
            UpdateTgCountStatus();
            if (_techLogDocs.Count > 0)
            {
                GridTg.SelectedIndex = 0;
            }

            if (ct.IsCancellationRequested)
            {
                TxtStatus.Text = $"⚠️ Парсинг остановлен пользователем! Загружено {_techLogDocs.Count} записей.";
            }
            else
            {
                TxtStatus.Text = $"Парсинг ТЖ завершен! Загружено {_techLogDocs.Count} записей (отсортировано: сначала новые).";
            }
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = $"⚠️ Парсинг остановлен пользователем. Загружено {_techLogDocs.Count} записей.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при парсинге ТЖ: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            TxtStatus.Text = "Ошибка парсинга ТЖ.";
        }
        finally
        {
            HideProgress();
            BtnParseTg.IsEnabled = true;
            BtnCancelTg.Visibility = Visibility.Collapsed;
            DeepGarbageCollection();
            _parsingCts?.Dispose();
            _parsingCts = null;
        }
    }

    /// <summary>
    /// Извлечение численного лимита количества записей из ComboBox.
    /// </summary>
    private static int GetPreviewLimit(ComboBox cmb)
    {
        if (cmb.SelectedItem is ComboBoxItem item && item.Content != null)
        {
            var text = item.Content.ToString() ?? string.Empty;
            var digits = new string(text.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var val) && val > 0)
                return val;
        }
        return int.MaxValue; // Без ограничений ("Все записи")
    }

    /// <summary>
    /// Высокопроизводительный потоковый парсинг Журнала Регистрации с возможностью мгновенной отмены.
    /// </summary>
    private async void BtnParseLg_Click(object sender, RoutedEventArgs e)
    {
        var path = TxtLgPath.Text.Trim();
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            MessageBox.Show($"Путь к ЖР не найден: {path}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dictPath = LogDiscovery.FindEventLogDictionary(path);
        if (string.IsNullOrEmpty(dictPath) || !File.Exists(dictPath))
        {
            MessageBox.Show($"Файл словаря 1Cv8.lgf не найден по пути: {path}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _parsingCts = new CancellationTokenSource();
        var ct = _parsingCts.Token;

        BtnParseLg.IsEnabled = false;
        BtnCancelLg.Visibility = Visibility.Visible;
        TxtStatus.Text = "Идет разбор словаря и параллельный парсинг записей Журнала Регистрации...";
        _eventLogDocs.Clear();
        _eventLogDocs.TrimExcess();
        DeepGarbageCollection();
        GridLg.ItemsSource = null;
        ShowProgress("Чтение словаря 1Cv8.lgf...");

        try
        {
            var dict = await EventLogParser.ParseDictionaryAsync(dictPath);
            var lgpFiles = LogDiscovery.FindEventLogFiles(path).ToList();

            if (lgpFiles.Count == 0)
            {
                MessageBox.Show($"Файлы событий (*.lgp) не найдены по пути: {path}", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Information);
                TxtStatus.Text = "Файлы ЖР не найдены.";
                return;
            }

            var maxDocs = GetPreviewLimit(CmbLgLimit);
            var totalBytes = lgpFiles.Sum(f =>
            {
                try { return new FileInfo(f).Length; } catch { return 0L; }
            });

            var maxDegree = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
            var channel = Channel.CreateBounded<EventLogDoc>(new BoundedChannelOptions(10_000)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true
            });

            long processedBytes = 0;
            int completedFiles = 0;

            var producerTask = Task.Run(async () =>
            {
                try
                {
                    await Parallel.ForEachAsync(lgpFiles, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = maxDegree,
                        CancellationToken = ct
                    }, async (lgpFile, token) =>
                    {
                        long fileLength = 0;
                        try { fileLength = new FileInfo(lgpFile).Length; } catch { }

                        await foreach (var doc in EventLogParser.ParseLogAsync(lgpFile, dict, ct: token))
                        {
                            await channel.Writer.WriteAsync(doc, token);
                        }

                        var currentDone = Interlocked.Increment(ref completedFiles);
                        var currentBytes = Interlocked.Add(ref processedBytes, fileLength);
                        var pct = totalBytes > 0 ? (double)currentBytes / totalBytes * 100.0 : 0.0;
                        UpdateProgress(pct, $"Файлов {currentDone}/{lgpFiles.Count}");
                    });
                }
                catch (OperationCanceledException) { }
                finally
                {
                    channel.Writer.Complete();
                }
            }, ct);

            var batch = new List<EventLogDoc>(500);
            try
            {
                while (await channel.Reader.WaitToReadAsync(ct))
                {
                    while (channel.Reader.TryRead(out var doc))
                    {
                        batch.Add(doc);
                        if (batch.Count >= 500)
                        {
                            _eventLogDocs.AddRange(batch);
                            batch.Clear();
                            UpdateLgCountStatus();
                            await Task.Yield(); // Освобождаем UI-поток
                            if (_eventLogDocs.Count >= maxDocs) break;
                        }
                    }
                    if (batch.Count > 0)
                    {
                        _eventLogDocs.AddRange(batch);
                        batch.Clear();
                        UpdateLgCountStatus();
                    }
                    if (_eventLogDocs.Count >= maxDocs) break;
                }
            }
            catch (OperationCanceledException) { }

            try { await producerTask; } catch (OperationCanceledException) { }

            // Сортировка по активным колонкам (Direct In-Memory Sort)
            if (_activeLgSorts.Count > 0)
            {
                SortEventLogDocs(_eventLogDocs, _activeLgSorts);
            }
            else
            {
                ApplyLgSortPreset();
            }

            PopulateLgEventFilterItems();
            PopulateLgExcludeFilterItems();
            PopulateLgFieldValues();
            _lgCollectionView = CollectionViewSource.GetDefaultView(_eventLogDocs);
            _lgCollectionView.Filter = FilterLgDoc;

            foreach (var col in GridLg.Columns)
            {
                var match = _activeLgSorts.FirstOrDefault(s => string.Equals(s.SortMemberPath, col.SortMemberPath, StringComparison.OrdinalIgnoreCase));
                col.SortDirection = match != default ? match.Direction : null;
            }

            GridLg.ItemsSource = _lgCollectionView;
            UpdateLgFilterChips();
            UpdateLgCountStatus();
            if (_eventLogDocs.Count > 0)
            {
                GridLg.SelectedIndex = 0;
            }

            if (ct.IsCancellationRequested)
            {
                TxtStatus.Text = $"⚠️ Парсинг остановлен пользователем! Загружено {_eventLogDocs.Count} записей.";
            }
            else
            {
                TxtStatus.Text = $"Парсинг ЖР завершен! Загружено {_eventLogDocs.Count} записей. Словарь: {dict.Users.Count} польз., {dict.Events.Count} соб.";
            }
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = $"⚠️ Парсинг остановлен пользователем. Загружено {_eventLogDocs.Count} записей.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при парсинге ЖР: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            TxtStatus.Text = "Ошибка парсинга ЖР.";
        }
        finally
        {
            HideProgress();
            BtnParseLg.IsEnabled = true;
            BtnCancelLg.Visibility = Visibility.Collapsed;
            DeepGarbageCollection();
            _parsingCts?.Dispose();
            _parsingCts = null;
        }
    }

    /// <summary>
    /// Ручной сброс и глубокая оптимизация оперативной памяти (LOH/Gen2 + Win32 WorkingSet Trim).
    /// </summary>
    private void BtnCleanMemory_Click(object sender, RoutedEventArgs e)
    {
        var beforeMb = GC.GetTotalMemory(false) / 1024.0 / 1024.0;
        DeepGarbageCollection();
        var afterMb = GC.GetTotalMemory(true) / 1024.0 / 1024.0;
        TxtStatus.Text = $"🧹 Память оптимизирована и освобождена ({beforeMb:F1} МБ -> {afterMb:F1} МБ).";
    }

    /// <summary>
    /// Переход в Telegram автора приложения (@DarkSailas).
    /// </summary>
    private void TgLink_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://t.me/DarkSailas",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось открыть Telegram ссылку @DarkSailas");
        }
    }

    /// <summary>
    /// Разовая отправка распарсенных (или выбранных) записей ТЖ в Elasticsearch с диалоговым подтверждением.
    /// </summary>
    private async void BtnExportTgElastic_Click(object sender, RoutedEventArgs e)
    {
        var selectedDocs = GridTg.SelectedItems.OfType<TechLogDoc>().ToList();
        var isSelectedScope = selectedDocs.Count > 0;
        var docsToSend = isSelectedScope ? selectedDocs : _techLogDocs;

        if (docsToSend.Count == 0)
        {
            MessageBox.Show("Сначала распарсьте Технологический Журнал (ТЖ) или выберите записи в таблице!", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settings = BuildElasticSettingsFromUi();
        var indexName = $"{settings.TechLogIndexPrefix}_gui_{DateTime.UtcNow:yyyyMMdd}";
        var scopeDescription = isSelectedScope ? $"выделенные {docsToSend.Count} записи(ей)" : $"все {docsToSend.Count} записи(ей) таблицы";

        var confirmResult = MessageBox.Show(
            $"Вы уверены, что хотите отправить {scopeDescription} Технологического Журнала в Elasticsearch / Kibana?\n\n" +
            $"• Сервер назначения: {settings.ServerUrl}\n" +
            $"• Целевой индекс: {indexName}\n" +
            $"• Размер пачки (Bulk): {settings.BulkBatchSize} документов",
            "Подтверждение отправки в Elastic",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmResult != MessageBoxResult.Yes)
        {
            TxtStatus.Text = "Отправка ТЖ отменена пользователем.";
            return;
        }

        BtnExportTgElastic.IsEnabled = false;
        TxtStatus.Text = $"Идет отправка {docsToSend.Count} записей ТЖ в Elasticsearch...";

        try
        {
            var publisher = new ElasticPublisher(settings, NullLogger<ElasticPublisher>.Instance);
            var (success, failed) = await publisher.BulkIndexTechLogAsync(indexName, docsToSend);

            var scopeText = isSelectedScope ? "выделенных" : "всех";
            MessageBox.Show($"Отправка записей ТЖ завершена!\n\n• Индекс: {indexName}\n• Успешно отправлено: {success} записей ({scopeText})\n• Сбоев: {failed}", "Отправка в Elastic", MessageBoxButton.OK, MessageBoxImage.Information);
            TxtStatus.Text = $"Успешно отправлено {success} записей ТЖ в индекс '{indexName}'.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при отправке в Elasticsearch:\n{ex.Message}", "Ошибка подключения", MessageBoxButton.OK, MessageBoxImage.Error);
            TxtStatus.Text = "Ошибка отправки в Elastic.";
        }
        finally
        {
            BtnExportTgElastic.IsEnabled = true;
        }
    }

    /// <summary>
    /// Разовая отправка распарсенных (или выбранных) записей ЖР в Elasticsearch с диалоговым подтверждением.
    /// </summary>
    private async void BtnExportLgElastic_Click(object sender, RoutedEventArgs e)
    {
        var selectedDocs = GridLg.SelectedItems.OfType<EventLogDoc>().ToList();
        var isSelectedScope = selectedDocs.Count > 0;
        var docsToSend = isSelectedScope ? selectedDocs : _eventLogDocs;

        if (docsToSend.Count == 0)
        {
            MessageBox.Show("Сначала распарсьте Журнал Регистрации (ЖР) или выберите записи в таблице!", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settings = BuildElasticSettingsFromUi();
        var indexName = $"{settings.EventLogIndexPrefix}_gui_{DateTime.UtcNow:yyyyMMdd}";
        var scopeDescription = isSelectedScope ? $"выделенные {docsToSend.Count} записи(ей)" : $"все {docsToSend.Count} записи(ей) таблицы";

        var confirmResult = MessageBox.Show(
            $"Вы уверены, что хотите отправить {scopeDescription} Журнала Регистрации в Elasticsearch / Kibana?\n\n" +
            $"• Сервер назначения: {settings.ServerUrl}\n" +
            $"• Целевой индекс: {indexName}\n" +
            $"• Размер пачки (Bulk): {settings.BulkBatchSize} документов",
            "Подтверждение отправки в Elastic",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmResult != MessageBoxResult.Yes)
        {
            TxtStatus.Text = "Отправка ЖР отменена пользователем.";
            return;
        }

        BtnExportLgElastic.IsEnabled = false;
        TxtStatus.Text = $"Идет отправка {docsToSend.Count} записей ЖР в Elasticsearch...";

        try
        {
            var publisher = new ElasticPublisher(settings, NullLogger<ElasticPublisher>.Instance);
            var (success, failed) = await publisher.BulkIndexEventLogAsync(indexName, docsToSend);

            var scopeText = isSelectedScope ? "выделенных" : "всех";
            MessageBox.Show($"Отправка записей ЖР завершена!\n\n• Индекс: {indexName}\n• Успешно отправлено: {success} записей ({scopeText})\n• Сбоев: {failed}", "Отправка в Elastic", MessageBoxButton.OK, MessageBoxImage.Information);
            TxtStatus.Text = $"Успешно отправлено {success} записей ЖР в индекс '{indexName}'.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при отправке в Elasticsearch:\n{ex.Message}", "Ошибка подключения", MessageBoxButton.OK, MessageBoxImage.Error);
            TxtStatus.Text = "Ошибка отправки в Elastic.";
        }
        finally
        {
            BtnExportLgElastic.IsEnabled = true;
        }
    }

    /// <summary>
    /// Разовая отправка всех текущих загруженных данных (ТЖ и ЖР) в Elasticsearch с подтверждением.
    /// </summary>
    private async void BtnExportAllElastic_Click(object sender, RoutedEventArgs e)
    {
        if (_techLogDocs.Count == 0 && _eventLogDocs.Count == 0)
        {
            MessageBox.Show("Сначала распарсьте ТЖ или ЖР для выгрузки данных!", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settings = BuildElasticSettingsFromUi();
        var tgIndex = $"{settings.TechLogIndexPrefix}_gui_{DateTime.UtcNow:yyyyMMdd}";
        var lgIndex = $"{settings.EventLogIndexPrefix}_gui_{DateTime.UtcNow:yyyyMMdd}";

        var confirmResult = MessageBox.Show(
            $"Вы уверены, что хотите массово выгрузить все распарсенные логи 1С в Elasticsearch / Kibana?\n\n" +
            $"• ТЖ ({_techLogDocs.Count} записей) -> Индекс: {tgIndex}\n" +
            $"• ЖР ({_eventLogDocs.Count} записей) -> Индекс: {lgIndex}\n" +
            $"• Сервер назначения: {settings.ServerUrl}",
            "Подтверждение массового экспорта",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmResult != MessageBoxResult.Yes)
        {
            TxtStatus.Text = "Массовая выгрузка отменена пользователем.";
            return;
        }

        BtnExportAllElastic.IsEnabled = false;
        TxtStatus.Text = "Идет массовая отправка логов 1С в Elasticsearch...";

        try
        {
            var publisher = new ElasticPublisher(settings, NullLogger<ElasticPublisher>.Instance);

            int tgSuccess = 0, tgFailed = 0, lgSuccess = 0, lgFailed = 0;

            if (_techLogDocs.Count > 0)
            {
                (tgSuccess, tgFailed) = await publisher.BulkIndexTechLogAsync(tgIndex, _techLogDocs);
            }

            if (_eventLogDocs.Count > 0)
            {
                (lgSuccess, lgFailed) = await publisher.BulkIndexEventLogAsync(lgIndex, _eventLogDocs);
            }

            MessageBox.Show($"Массовая выгрузка логов 1С в Elasticsearch завершена!\n\n• ТЖ: {tgSuccess} отправлено в '{tgIndex}' (Сбоев: {tgFailed})\n• ЖР: {lgSuccess} отправлено в '{lgIndex}' (Сбоев: {lgFailed})", "Выгрузка в Elastic", MessageBoxButton.OK, MessageBoxImage.Information);
            TxtStatus.Text = $"Массовая выгрузка завершена: {tgSuccess} ТЖ, {lgSuccess} ЖР.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка массовой выгрузки в Elasticsearch:\n{ex.Message}", "Ошибка подключения", MessageBoxButton.OK, MessageBoxImage.Error);
            TxtStatus.Text = "Ошибка массовой выгрузки.";
        }
        finally
        {
            BtnExportAllElastic.IsEnabled = true;
        }
    }

    /// <summary>
    /// Отображение выбранной строки ТЖ в виде форматированного JSON.
    /// </summary>
    private void GridTg_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
    }

    private void GridLg_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
    }

    /// <summary>
    /// Открытие веб-интерфейса Kibana в системном браузере.
    /// </summary>
    private void BtnOpenKibana_Click(object sender, RoutedEventArgs e)
    {
        var url = TxtKibanaUrl.Text.Trim();
        if (string.IsNullOrEmpty(url)) url = "http://localhost:5601";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            TxtStatus.Text = $"Открываем веб-интерфейс Kibana: {url}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось открыть браузер: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Проверка сетевой доступности сервера Kibana по HTTP.
    /// </summary>
    private async void BtnTestKibana_Click(object sender, RoutedEventArgs e)
    {
        var url = TxtKibanaUrl.Text.Trim();
        if (string.IsNullOrEmpty(url)) url = "http://localhost:5601";

        TxtStatus.Text = $"Проверка соединения с Kibana по адресу {url}...";

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await client.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                MessageBox.Show($"Соединение с Kibana успешно установлено!\n\nСтатус: {(int)response.StatusCode} {response.ReasonPhrase}", "Успешное подключение", MessageBoxButton.OK, MessageBoxImage.Information);
                TxtStatus.Text = "Подключение к Kibana подтверждено!";
            }
            else
            {
                MessageBox.Show($"Сервер Kibana ответил кодом: {(int)response.StatusCode} {response.ReasonPhrase}", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtStatus.Text = "Сервер Kibana вернул предупреждение.";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка соединения с Kibana ({url}):\n{ex.Message}", "Ошибка подключения", MessageBoxButton.OK, MessageBoxImage.Error);
            TxtStatus.Text = "Ошибка подключения к Kibana.";
        }
    }

    /// <summary>
    /// Проверка сетевого соединения с Elasticsearch / OpenSearch по HTTP.
    /// </summary>
    private async void BtnTestElastic_Click(object sender, RoutedEventArgs e)
    {
        var url = TxtElasticUrl.Text.Trim();
        if (string.IsNullOrEmpty(url)) url = "http://localhost:9200";

        var user = TxtElasticUser.Text.Trim();
        var pass = TxtElasticPass.Password.Trim();
        var apiKey = TxtElasticApiKey.Text.Trim();

        TxtStatus.Text = $"Проверка соединения с Elasticsearch по адресу {url}...";

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass))
            {
                var authBytes = Encoding.UTF8.GetBytes($"{user}:{pass}");
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
            }
            else if (!string.IsNullOrEmpty(apiKey))
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("ApiKey", apiKey);
            }

            var response = await client.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var authInfo = !string.IsNullOrEmpty(user) ? $" (Авторизован как {user})" : " (Без авторизации)";
                MessageBox.Show($"Соединение с Elasticsearch / OpenSearch успешно установлено!{authInfo}\n\nОтвет сервера:\n{content[..Math.Min(250, content.Length)]}...", "Успешное подключение", MessageBoxButton.OK, MessageBoxImage.Information);
                TxtStatus.Text = "Подключение к Elastic/OpenSearch подтверждено!";
            }
            else
            {
                MessageBox.Show($"Сервер ответил кодом: {(int)response.StatusCode} {response.ReasonPhrase}", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtStatus.Text = "Сервер Elastic вернул ошибки авторизации или доступа.";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка соединения с Elasticsearch ({url}):\n{ex.Message}", "Ошибка подключения", MessageBoxButton.OK, MessageBoxImage.Error);
            TxtStatus.Text = "Ошибка подключения к Elastic.";
        }
    }

    /// <summary>
    /// Сохранение измененных параметров (Elasticsearch, Kibana, FileDump ротация) в appsettings.json.
    /// </summary>
    private async void BtnSaveConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(configPath))
            {
                MessageBox.Show("Файл appsettings.json не найден в рабочей директории!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var jsonString = await File.ReadAllTextAsync(configPath);
            var rootNode = JsonNode.Parse(jsonString);

            if (rootNode is JsonObject rootObj)
            {
                var exporterObj = rootObj["Exporter"] as JsonObject ?? rootObj;

                if (exporterObj["TechLog"] is JsonObject tgObj)
                {
                    tgObj["DirectoryPath"] = TxtTgPath.Text.Trim();
                }

                if (exporterObj["EventLog"] is JsonObject evObj)
                {
                    evObj["DirectoryPath"] = TxtLgPath.Text.Trim();
                }

                if (exporterObj["Elastic"] is JsonObject elasticObj)
                {
                    elasticObj["Enabled"] = ChkElasticEnabled.IsChecked ?? true;
                    elasticObj["ServerUrl"] = TxtElasticUrl.Text.Trim();
                    elasticObj["Username"] = TxtElasticUser.Text.Trim();
                    elasticObj["Password"] = TxtElasticPass.Password.Trim();
                    elasticObj["ApiKey"] = TxtElasticApiKey.Text.Trim();
                    elasticObj["EventLogIndexPrefix"] = TxtEventLogPrefix.Text.Trim();
                    elasticObj["TechLogIndexPrefix"] = TxtTechLogPrefix.Text.Trim();
                }

                if (exporterObj["Kibana"] is JsonObject kibanaObj)
                {
                    kibanaObj["ServerUrl"] = TxtKibanaUrl.Text.Trim();
                }

                if (exporterObj["FileDump"] is JsonObject dumpObj)
                {
                    dumpObj["DirectoryPath"] = TxtDumpDirectory.Text.Trim();
                }

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
                };

                await File.WriteAllTextAsync(configPath, rootObj.ToJsonString(options), Encoding.UTF8);
            }

            MessageBox.Show("Конфигурация (Elasticsearch, Kibana, FileDump ротация дампов) успешно сохранена в appsettings.json!", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            TxtStatus.Text = "Настройки выгрузки и ротации сохранены в appsettings.json.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при сохранении конфигурации: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Экспорт выбранных строк или всей таблицы Технологического Журнала (ТЖ) в JSON / JSONL.
    /// </summary>
    private async void BtnExportTgJson_Click(object sender, RoutedEventArgs e)
    {
        await ExportTgDocsToJsonAsync();
    }

    /// <summary>
    /// Экспорт выбранных строк или всей таблицы Журнала Регистрации (ЖР) в JSON / JSONL.
    /// </summary>
    private async void BtnExportLgJson_Click(object sender, RoutedEventArgs e)
    {
        await ExportLgDocsToJsonAsync();
    }

    /// <summary>
    /// Универсальный экспорт активной вкладки или всех распарсенных данных в JSON файл.
    /// </summary>
    private async void BtnSaveJson_Click(object sender, RoutedEventArgs e)
    {
        if (MainTabControl?.SelectedIndex == 0)
        {
            await ExportTgDocsToJsonAsync();
            return;
        }

        if (MainTabControl?.SelectedIndex == 1)
        {
            await ExportLgDocsToJsonAsync();
            return;
        }

        // Если открыты настройки или другое - общий экспорт всех данных
        if (_techLogDocs.Count == 0 && _eventLogDocs.Count == 0)
        {
            MessageBox.Show("Сначала распарсите ТЖ или ЖР для создания дампа!", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var saveDialog = new SaveFileDialog
        {
            Title = "Сохранить общий JSON Lines Дамп",
            Filter = "JSON Lines файлы (*.jsonl)|*.jsonl|Форматированный JSON (*.json)|*.json|Все файлы (*.*)|*.*",
            FileName = $"ones_log_dump_all_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl"
        };

        if (saveDialog.ShowDialog() == true)
        {
            try
            {
                var isJsonLines = saveDialog.FileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
                if (isJsonLines)
                {
                    var compactOptions = new JsonSerializerOptions
                    {
                        WriteIndented = false,
                        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
                    };

                    var sb = new StringBuilder();
                    foreach (var doc in _techLogDocs)
                    {
                        sb.AppendLine(JsonSerializer.Serialize(doc, compactOptions));
                    }
                    foreach (var doc in _eventLogDocs)
                    {
                        sb.AppendLine(JsonSerializer.Serialize(doc, compactOptions));
                    }

                    await File.WriteAllTextAsync(saveDialog.FileName, sb.ToString(), Encoding.UTF8);
                }
                else
                {
                    var allDocs = new
                    {
                        TechLog = _techLogDocs,
                        EventLog = _eventLogDocs
                    };
                    var json = JsonSerializer.Serialize(allDocs, PrettyJson);
                    await File.WriteAllTextAsync(saveDialog.FileName, json, Encoding.UTF8);
                }

                var totalCount = _techLogDocs.Count + _eventLogDocs.Count;
                MessageBox.Show($"Дамп ({totalCount} записей) успешно сохранен в файл:\n{saveDialog.FileName}", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
                TxtStatus.Text = $"Сохранен общий дамп ({totalCount} строк).";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при сохранении: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async Task ExportTgDocsToJsonAsync()
    {
        var selectedDocs = GridTg.SelectedItems.OfType<TechLogDoc>().ToList();
        var isSelected = selectedDocs.Count > 0;
        var docsToExport = isSelected
            ? selectedDocs
            : (_tgCollectionView?.Cast<TechLogDoc>().ToList() ?? _techLogDocs);

        if (docsToExport.Count == 0)
        {
            MessageBox.Show("Нет данных Технологического Журнала (ТЖ) для экспорта! Сначала распарсьте логи или выберите строки.", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var scopePrefix = isSelected ? $"selected_{docsToExport.Count}" : "table";
        var saveDialog = new SaveFileDialog
        {
            Title = isSelected ? $"Экспорт {docsToExport.Count} выделенных строк ТЖ в JSON" : $"Экспорт таблицы ТЖ ({docsToExport.Count} строк) в JSON",
            Filter = "JSON Lines файлы (*.jsonl)|*.jsonl|Форматированный JSON (*.json)|*.json|Все файлы (*.*)|*.*",
            FileName = $"ones_techlog_{scopePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl"
        };

        if (saveDialog.ShowDialog() == true)
        {
            try
            {
                var isJsonLines = saveDialog.FileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
                if (isJsonLines)
                {
                    var compactOptions = new JsonSerializerOptions
                    {
                        WriteIndented = false,
                        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
                    };
                    var sb = new StringBuilder();
                    foreach (var doc in docsToExport)
                    {
                        sb.AppendLine(JsonSerializer.Serialize(doc, compactOptions));
                    }
                    await File.WriteAllTextAsync(saveDialog.FileName, sb.ToString(), Encoding.UTF8);
                }
                else
                {
                    var json = JsonSerializer.Serialize(docsToExport, PrettyJson);
                    await File.WriteAllTextAsync(saveDialog.FileName, json, Encoding.UTF8);
                }

                var scopeText = isSelected ? $"выделенные {docsToExport.Count} строк(и)" : $"все {docsToExport.Count} строк(и) таблицы";
                MessageBox.Show($"Экспорт ТЖ успешно завершен!\n\n• Сохранены: {scopeText}\n• Файл: {saveDialog.FileName}", "Экспорт в JSON", MessageBoxButton.OK, MessageBoxImage.Information);
                TxtStatus.Text = $"Экспортировано {docsToExport.Count} записей ТЖ в JSON.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при экспорте ТЖ в JSON:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async Task ExportLgDocsToJsonAsync()
    {
        var selectedDocs = GridLg.SelectedItems.OfType<EventLogDoc>().ToList();
        var isSelected = selectedDocs.Count > 0;
        var docsToExport = isSelected
            ? selectedDocs
            : (_lgCollectionView?.Cast<EventLogDoc>().ToList() ?? _eventLogDocs);

        if (docsToExport.Count == 0)
        {
            MessageBox.Show("Нет данных Журнала Регистрации (ЖР) для экспорта! Сначала распарсьте логи или выберите строки.", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var scopePrefix = isSelected ? $"selected_{docsToExport.Count}" : "table";
        var saveDialog = new SaveFileDialog
        {
            Title = isSelected ? $"Экспорт {docsToExport.Count} выделенных строк ЖР в JSON" : $"Экспорт таблицы ЖР ({docsToExport.Count} строк) в JSON",
            Filter = "JSON Lines файлы (*.jsonl)|*.jsonl|Форматированный JSON (*.json)|*.json|Все файлы (*.*)|*.*",
            FileName = $"ones_eventlog_{scopePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl"
        };

        if (saveDialog.ShowDialog() == true)
        {
            try
            {
                var isJsonLines = saveDialog.FileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
                if (isJsonLines)
                {
                    var compactOptions = new JsonSerializerOptions
                    {
                        WriteIndented = false,
                        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
                    };
                    var sb = new StringBuilder();
                    foreach (var doc in docsToExport)
                    {
                        sb.AppendLine(JsonSerializer.Serialize(doc, compactOptions));
                    }
                    await File.WriteAllTextAsync(saveDialog.FileName, sb.ToString(), Encoding.UTF8);
                }
                else
                {
                    var json = JsonSerializer.Serialize(docsToExport, PrettyJson);
                    await File.WriteAllTextAsync(saveDialog.FileName, json, Encoding.UTF8);
                }

                var scopeText = isSelected ? $"выделенные {docsToExport.Count} строк(и)" : $"все {docsToExport.Count} строк(и) таблицы";
                MessageBox.Show($"Экспорт ЖР успешно завершен!\n\n• Сохранены: {scopeText}\n• Файл: {saveDialog.FileName}", "Экспорт в JSON", MessageBoxButton.OK, MessageBoxImage.Information);
                TxtStatus.Text = $"Экспортировано {docsToExport.Count} записей ЖР в JSON.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при экспорте ЖР в JSON:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    /// <summary>
    /// Отображение модального окна профиля события (Dribbble Profile Information Popup).
    /// </summary>
    private void ShowDocDetails(object? docObj)
    {
        if (docObj == null) return;

        string title = "Детализация события 1С";
        string subTitle = "";
        string jsonText = "";
        string contextText = "";
        List<KeyValuePair<string, string>> propsList = [];

        if (docObj is TechLogDoc tg)
        {
            var longInfo = !string.IsNullOrEmpty(tg.LongInfoName) ? $" [Действие: {tg.LongInfoName}]" : "";
            title = $"Событие ТЖ: {tg.Event}{longInfo}";
            subTitle = $"{tg.DateFormatted} | Статус: {tg.ExecutionStatus} | Процесс: {tg.ProcessName} (PID {tg.ProcessId}, OSThread {tg.OSThread}) | Длительность: {tg.DurationFormatted}";
            jsonText = JsonSerializer.Serialize(tg, PrettyJson);

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(tg.LongInfoName)) sb.AppendLine($"=== ЦЕЛЕВОЕ ДЕЙСТВИЕ (LONGDURATIONINFO) ===\n{tg.LongInfoName} (Текущее ожидание: {tg.LongInfoWait} мкс)\n");
            if (!string.IsNullOrWhiteSpace(tg.Context)) sb.AppendLine($"=== КОНТЕКСТ ===\n{tg.Context}\n");
            if (!string.IsNullOrWhiteSpace(tg.Sql)) sb.AppendLine($"=== SQL ЗАПРОС ===\n{tg.Sql}\n");
            if (!string.IsNullOrWhiteSpace(tg.Locks)) sb.AppendLine($"=== БЛОКИРОВКИ ===\n{tg.Locks}\n");
            if (!string.IsNullOrWhiteSpace(tg.Descr)) sb.AppendLine($"=== ОПИСАНИЕ / ОШИБКА ===\n{tg.Descr}\n");
            if (!string.IsNullOrWhiteSpace(tg.WaitConnections)) sb.AppendLine($"=== ОЖИДАНИЕ СОЕДИНЕНИЙ ===\n{tg.WaitConnections}\n");
            contextText = sb.Length > 0 ? sb.ToString() : "(Дополнительный контекст отсутствует)";

            propsList =
            [
                new("Событие (Event)", tg.Event),
                new("Статус операции", tg.ExecutionStatus),
                new("Дата и время", tg.DateFormatted),
                new("Длительность", tg.DurationFormatted),
                new("Процесс 1С", tg.ProcessName ?? ""),
                new("PID процесса (Диспетчер задач Windows)", tg.ProcessId ?? ""),
                new("SPID СУБД (Соединение в MS SQL / PostgreSQL)", !string.IsNullOrEmpty(tg.Spid) ? tg.Spid : "— (СУБД не задействована)"),
                new("Поток ОС (OSThread)", tg.OSThread ?? ""),
                new("Действие (LongInfoName)", tg.LongInfoName ?? ""),
                new("Пользователь (Usr)", tg.User ?? ""),
                new("Приложение (App)", tg.App ?? ""),
                new("Сеанс 1С (SessionID)", tg.SessionId ?? ""),
                new("ID соединения 1С (ConnectID)", tg.ConnectId ?? "")
            ];

            foreach (var (k, v) in tg.Properties)
            {
                propsList.Add(new(k, v));
            }
        }
        else if (docObj is EventLogDoc lg)
        {
            title = $"Событие ЖР: {lg.Event}";
            subTitle = $"{lg.DateFormatted} | Важность: {lg.Importance} | Пользователь: {lg.User} | Мета: {lg.Meta}";
            jsonText = JsonSerializer.Serialize(lg, PrettyJson);
            contextText = string.IsNullOrWhiteSpace(lg.Comment) ? "(Комментарий отсутствует)" : lg.Comment;

            propsList =
            [
                new("Событие (Event)", lg.Event ?? ""),
                new("Дата и время", lg.DateFormatted),
                new("Важность (Importance)", lg.Importance ?? ""),
                new("Пользователь (User)", lg.User ?? ""),
                new("Приложение (App)", lg.App ?? ""),
                new("Метаданные (Meta)", lg.Meta ?? ""),
                new("Сеанс (Session)", lg.Session ?? ""),
                new("Транзакция (Tran)", lg.Tran ?? ""),
                new("Данные (Data)", lg.Data ?? "")
            ];
        }

        TxtModalTitle.Text = title;
        TxtModalSubTitle.Text = subTitle;
        TxtModalJson.Text = jsonText;
        TxtModalContext.Text = contextText;
        GridModalProps.ItemsSource = propsList;

        OverlayDetails.Visibility = Visibility.Visible;
    }

    private void BtnInspectRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext != null)
        {
            ShowDocDetails(btn.DataContext);
        }
    }

    private void GridTg_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ShowDocDetails(GridTg.SelectedItem);
    }

    private void GridLg_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ShowDocDetails(GridLg.SelectedItem);
    }

    private void BtnCloseModal_Click(object sender, RoutedEventArgs e)
    {
        OverlayDetails.Visibility = Visibility.Collapsed;
    }

    private void OverlayDetails_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource == OverlayDetails)
        {
            OverlayDetails.Visibility = Visibility.Collapsed;
        }
    }

    private void BtnCopyJson_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TxtModalJson.Text))
        {
            Clipboard.SetText(TxtModalJson.Text);
            TxtStatus.Text = "📋 Форматированный JSON скопирован в буфер обмена.";
        }
    }

    private void BtnCopyModalContext_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TxtModalContext.Text))
        {
            Clipboard.SetText(TxtModalContext.Text);
            TxtStatus.Text = "🔍 Контекст и SQL запрос скопированы в буфер обмена.";
        }
    }

    private void BtnCopyModalAll_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== {TxtModalTitle.Text} ===");
        sb.AppendLine(TxtModalSubTitle.Text);
        sb.AppendLine();
        sb.AppendLine("--- СВОЙСТВА СОБЫТИЯ ---");
        if (GridModalProps.ItemsSource is IEnumerable<KeyValuePair<string, string>> props)
        {
            foreach (var kv in props)
            {
                sb.AppendLine($"{kv.Key}: {kv.Value}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("--- КОНТЕКСТ / SQL ---");
        sb.AppendLine(TxtModalContext.Text);

        Clipboard.SetText(sb.ToString());
        TxtStatus.Text = "📑 Все данные и свойства события скопированы в буфер обмена.";
    }

    private string _lastClickedPropCellText = "";
    private KeyValuePair<string, string>? _lastClickedPropPair;

    private void GridModalProps_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep)
        {
            var cell = FindVisualParent<DataGridCell>(dep);
            if (cell != null)
            {
                if (cell.Content is TextBlock tb)
                {
                    _lastClickedPropCellText = tb.Text;
                }
            }

            var row = FindVisualParent<DataGridRow>(dep);
            if (row != null && row.Item is KeyValuePair<string, string> kv)
            {
                _lastClickedPropPair = kv;
                if (!GridModalProps.SelectedItems.Contains(kv))
                {
                    GridModalProps.SelectedItem = kv;
                }
            }
        }
    }

    private void GridModalProps_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            CopyModalPropsSelection();
            e.Handled = true;
        }
    }

    private void ModalPropsMenuCopyVal_Click(object sender, RoutedEventArgs e)
    {
        if (_lastClickedPropPair.HasValue)
        {
            Clipboard.SetText(_lastClickedPropPair.Value.Value);
            TxtStatus.Text = $"📋 Скопировано значение: {_lastClickedPropPair.Value.Value}";
        }
        else if (!string.IsNullOrEmpty(_lastClickedPropCellText))
        {
            Clipboard.SetText(_lastClickedPropCellText);
            TxtStatus.Text = $"📋 Скопировано значение: {_lastClickedPropCellText}";
        }
    }

    private void ModalPropsMenuCopyKey_Click(object sender, RoutedEventArgs e)
    {
        if (_lastClickedPropPair.HasValue)
        {
            Clipboard.SetText(_lastClickedPropPair.Value.Key);
            TxtStatus.Text = $"🔑 Скопировано имя свойства: {_lastClickedPropPair.Value.Key}";
        }
    }

    private void ModalPropsMenuCopyRow_Click(object sender, RoutedEventArgs e)
    {
        CopyModalPropsSelection();
    }

    private void ModalPropsMenuCopyAllJson_Click(object sender, RoutedEventArgs e)
    {
        if (GridModalProps.ItemsSource is IEnumerable<KeyValuePair<string, string>> props)
        {
            var dict = props.ToDictionary(k => k.Key, v => v.Value);
            var json = JsonSerializer.Serialize(dict, PrettyJson);
            Clipboard.SetText(json);
            TxtStatus.Text = "📦 Все свойства события скопированы в формате JSON.";
        }
    }

    private void CopyModalPropsSelection()
    {
        var items = GridModalProps.SelectedItems.Cast<KeyValuePair<string, string>>().ToList();
        if (items.Count == 0 && _lastClickedPropPair.HasValue) items.Add(_lastClickedPropPair.Value);
        if (items.Count == 0) return;

        var sb = new StringBuilder();
        foreach (var p in items)
        {
            sb.AppendLine($"{p.Key}\t{p.Value}");
        }
        Clipboard.SetText(sb.ToString().TrimEnd());
        TxtStatus.Text = $"📑 Скопировано {items.Count} строк свойств в буфер обмена.";
    }

    /// <summary>
    /// Справочник описаний для известных событий Технологического Журнала 1С.
    /// </summary>
    private static readonly Dictionary<string, string> KnownTgEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EXCP"] = "Ошибки платформы и конфигурации",
        ["EXCPCNTX"] = "Контекст места возникновения ошибки",
        ["QERR"] = "Ошибки компиляции/выполнения запросов",
        ["DBMSSQL"] = "Запросы к СУБД MS SQL Server",
        ["DBPOSTGRS"] = "Запросы к СУБД PostgreSQL",
        ["DBORACLE"] = "Запросы к СУБД Oracle Database",
        ["DBIBMDB2"] = "Запросы к СУБД IBM DB2",
        ["DBV8DBENG"] = "Запросы файловой СУБД 1С",
        ["SDBL"] = "Запросы внутреннего языка 1С (SDBL)",
        ["TLOCK"] = "Установка и ожидание блокировок 1С",
        ["TDEADLOCK"] = "Взаимоблокировки ресурсов (Deadlock)",
        ["TTIMEOUT"] = "Таймаут ожидания блокировки ресурса",
        ["CALL"] = "Серверные контекстные вызовы (CALL)",
        ["SCALL"] = "Удаленные вызовы процедур кластера",
        ["RUNMETH"] = "Исполнение процедур/функций модулей",
        ["LONGDURATIONINFO"] = "Длительная выполняющаяся операция",
        ["ADDIN"] = "Вызовы внешних компонент (Native/COM)",
        ["ADMIN"] = "Команды администрирования кластера",
        ["ATTN"] = "Предупреждения и системные уведомления",
        ["Context"] = "Стек контекста выполнения кода",
        ["CONN"] = "Установка и разрыв соединений",
        ["SESN"] = "Создание и завершение сеансов 1С",
        ["MEM"] = "Потребление оперативной памяти",
        ["PROC"] = "События рабочих процессов (rphost)",
        ["VRSREQUEST"] = "Входящие HTTP/Web-сервисы запросы",
        ["VRSRESPONSE"] = "Ответы HTTP/Web-сервисов",
        ["HASP"] = "Проверка аппаратных ключей защиты HASP",
        ["LIC"] = "Проверка программных лицензий 1С",
        ["PERF"] = "Замеры счетчиков производительности",
        ["LEAKS"] = "Утечки дескрипторов и памяти",
        ["SCOM"] = "Сетевой обмен между процессами кластера",
        ["SDISPATCH"] = "Диспетчеризация клиентских запросов",
        ["CLSTR"] = "Состояние и топология кластера 1С",
        ["RESTP"] = "Перезапуск и ротация рабочих процессов"
    };

    private void PopulateTgEventFilterItems()
    {
        if (CmbTgEventFilter == null) return;

        CmbTgEventFilter.SelectionChanged -= CmbTgEventFilter_SelectionChanged;

        var selectedText = (CmbTgEventFilter.SelectedItem as ComboBoxItem)?.Content?.ToString()
            ?? (CmbTgEventFilter.SelectedItem as string);
        var selectedTag = (CmbTgEventFilter.SelectedItem as ComboBoxItem)?.Tag as string;

        CmbTgEventFilter.Items.Clear();
        CmbTgEventFilter.Items.Add(new ComboBoxItem { Content = "Все события 1С" });
        CmbTgEventFilter.Items.Add(new ComboBoxItem { Content = "Ошибки (EXCP / EXCPCNTX / QERR)" });
        CmbTgEventFilter.Items.Add(new ComboBoxItem { Content = "Блокировки (TLOCK / TDEADLOCK / TTIMEOUT)" });
        CmbTgEventFilter.Items.Add(new ComboBoxItem { Content = "Запросы СУБД (DBMSSQL / SDBL / DBPOSTGRS)" });
        CmbTgEventFilter.Items.Add(new ComboBoxItem { Content = "Вызовы (CALL / SCALL / RUNMETH)" });
        CmbTgEventFilter.Items.Add(new ComboBoxItem { Content = "Длительные (LONGDURATIONINFO)" });

        if (_techLogDocs.Count == 0)
        {
            // Логи еще не загружены — показываем полный справочник всех известных событий 1С
            foreach (var (ev, desc) in KnownTgEvents.OrderBy(k => k.Key))
            {
                CmbTgEventFilter.Items.Add(new ComboBoxItem
                {
                    Content = $"{ev} — {desc}",
                    Tag = ev
                });
            }
        }
        else
        {
            // Логи загружены: подсчитываем количество событий
            var eventCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var doc in _techLogDocs)
            {
                if (string.IsNullOrWhiteSpace(doc.Event)) continue;
                eventCounts[doc.Event] = eventCounts.GetValueOrDefault(doc.Event, 0) + 1;
            }

            // 1. Сначала события, найденные в текущем логе (сверху, отсортированные по алфавиту)
            var presentEvents = eventCounts.Keys.OrderBy(e => e).ToList();
            foreach (var ev in presentEvents)
            {
                var count = eventCounts[ev];
                string itemContent = KnownTgEvents.TryGetValue(ev, out var desc)
                    ? $"{ev} — {desc} ({count:N0})"
                    : $"{ev} ({count:N0})";

                CmbTgEventFilter.Items.Add(new ComboBoxItem { Content = itemContent, Tag = ev });
            }

            // 2. Затем остальные события из справочника (со счетчиком 0)
            var absentKnown = KnownTgEvents.Keys
                .Where(k => !eventCounts.ContainsKey(k))
                .OrderBy(k => k);

            foreach (var ev in absentKnown)
            {
                var desc = KnownTgEvents[ev];
                CmbTgEventFilter.Items.Add(new ComboBoxItem
                {
                    Content = $"{ev} — {desc} (0)",
                    Tag = ev
                });
            }
        }

        int initialIndex = CmbTgEventFilter.SelectedIndex;
        int targetIndex = 0;
        if (!string.IsNullOrEmpty(selectedTag))
        {
            for (int i = 0; i < CmbTgEventFilter.Items.Count; i++)
            {
                if (CmbTgEventFilter.Items[i] is ComboBoxItem item && string.Equals(item.Tag as string, selectedTag, StringComparison.OrdinalIgnoreCase))
                {
                    targetIndex = i;
                    break;
                }
            }
        }
        
        if (targetIndex == 0 && !string.IsNullOrEmpty(selectedText))
        {
            for (int i = 0; i < CmbTgEventFilter.Items.Count; i++)
            {
                if (CmbTgEventFilter.Items[i] is ComboBoxItem item && string.Equals(item.Content?.ToString(), selectedText, StringComparison.OrdinalIgnoreCase))
                {
                    targetIndex = i;
                    break;
                }
            }
        }

        if (targetIndex == 0 && initialIndex > 0 && initialIndex < 6)
        {
            targetIndex = initialIndex;
        }

        CmbTgEventFilter.SelectedIndex = targetIndex;
        CmbTgEventFilter.SelectionChanged += CmbTgEventFilter_SelectionChanged;
    }

    private void PopulateTgExcludeFilterItems()
    {
        if (ListTgExcludeEvents == null) return;

        var previouslyChecked = _tgExcludeEventItems.Where(x => x.IsChecked).Select(x => x.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _tgExcludeEventItems.Clear();

        foreach (var (ev, desc) in KnownTgEvents.OrderBy(k => k.Key))
        {
            _tgExcludeEventItems.Add(new FilterCheckItem
            {
                Tag = ev,
                Title = ev,
                Subtitle = desc,
                IsChecked = previouslyChecked.Contains(ev)
            });
        }

        FilterTgExcludeEventList();
    }

    private void TxtTgExcludeEventSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterTgExcludeEventList();
    }

    private void FilterTgExcludeEventList()
    {
        if (ListTgExcludeEvents == null) return;
        var search = TxtTgExcludeEventSearch?.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(search))
        {
            ListTgExcludeEvents.ItemsSource = _tgExcludeEventItems;
        }
        else
        {
            ListTgExcludeEvents.ItemsSource = _tgExcludeEventItems.Where(x =>
                x.Tag.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                x.Subtitle.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    private void BtnTgExcludeClearAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _tgExcludeEventItems)
        {
            item.IsChecked = false;
        }
        UpdateTgFilterChips();
        _tgCollectionView?.Refresh();
        UpdateTgCountStatus();
    }

    private void ChkTgExcludeEventItem_Click(object sender, RoutedEventArgs e)
    {
        UpdateTgFilterChips();
        _tgCollectionView?.Refresh();
        UpdateTgCountStatus();
    }

    private void ChkTgFilter_Click(object sender, RoutedEventArgs e)
    {
        UpdateTgFilterChips();
        _tgCollectionView?.Refresh();
        UpdateTgCountStatus();
    }

    #region Отбор по полям ТЖ (Пользователи, Приложения, PID, SPID, OSThread)

    public void PopulateTgFieldValues()
    {
        if (_techLogDocs.Count == 0) return;

        PopulateFieldList(_techLogDocs.Select(x => x.User ?? ""), _tgUserItems, "Пользователь", ListTgUsers);
        PopulateFieldList(_techLogDocs.Select(x => x.App ?? ""), _tgAppItems, "Приложение", ListTgApps);
        PopulateFieldList(_techLogDocs.Select(x => x.ProcessId ?? ""), _tgPidItems, "PID", ListTgPids);
        PopulateFieldList(_techLogDocs.Select(x => x.Spid ?? ""), _tgSpidItems, "SPID", ListTgSpids);
        PopulateFieldList(_techLogDocs.Select(x => x.OSThread ?? ""), _tgThreadItems, "Поток OS", ListTgThreads);
    }

    private static void PopulateFieldList(IEnumerable<string> values, ObservableCollection<FieldValueItem> collection, string category, ItemsControl? control)
    {
        var incSet = collection.Where(x => x.IsInclude).Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exSet = collection.Where(x => x.IsExclude).Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        collection.Clear();

        var groups = values
            .GroupBy(x => x)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key);

        foreach (var g in groups)
        {
            var val = g.Key;
            collection.Add(new FieldValueItem
            {
                Category = category,
                Value = val,
                Count = g.Count(),
                IsInclude = incSet.Contains(val),
                IsExclude = exSet.Contains(val)
            });
        }

        if (control != null) control.ItemsSource = collection;
    }

    private void ChkTgFieldValueItem_Click(object sender, RoutedEventArgs e)
    {
        UpdateTgFilterChips();
        _tgCollectionView?.Refresh();
        UpdateTgCountStatus();
    }

    private void BtnTgClearAllFieldFilters_Click(object sender, RoutedEventArgs e)
    {
        ClearFieldCollection(_tgUserItems, true);
        ClearFieldCollection(_tgAppItems, true);
        ClearFieldCollection(_tgPidItems, true);
        ClearFieldCollection(_tgSpidItems, true);
        ClearFieldCollection(_tgThreadItems, true);
    }

    private void TxtTgSearchUsers_TextChanged(object sender, TextChangedEventArgs e) => FilterFieldControl(TxtTgSearchUsers.Text, _tgUserItems, ListTgUsers);
    private void TxtTgSearchApps_TextChanged(object sender, TextChangedEventArgs e) => FilterFieldControl(TxtTgSearchApps.Text, _tgAppItems, ListTgApps);
    private void TxtTgSearchPids_TextChanged(object sender, TextChangedEventArgs e) => FilterFieldControl(TxtTgSearchPids.Text, _tgPidItems, ListTgPids);
    private void TxtTgSearchSpids_TextChanged(object sender, TextChangedEventArgs e) => FilterFieldControl(TxtTgSearchSpids.Text, _tgSpidItems, ListTgSpids);
    private void TxtTgSearchThreads_TextChanged(object sender, TextChangedEventArgs e) => FilterFieldControl(TxtTgSearchThreads.Text, _tgThreadItems, ListTgThreads);

    private static void FilterFieldControl(string query, ObservableCollection<FieldValueItem> source, ItemsControl? control)
    {
        if (control == null) return;
        query = query?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(query))
        {
            control.ItemsSource = source;
        }
        else
        {
            control.ItemsSource = source.Where(x => x.Value.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    private void BtnTgSelectAllUsers_Click(object sender, RoutedEventArgs e) => SetFieldCollectionInclude(_tgUserItems, true);
    private void BtnTgExcludeAllUsers_Click(object sender, RoutedEventArgs e) => SetFieldCollectionExclude(_tgUserItems, true);
    private void BtnTgClearUsers_Click(object sender, RoutedEventArgs e) => ClearFieldCollection(_tgUserItems, true);

    private void BtnTgSelectAllApps_Click(object sender, RoutedEventArgs e) => SetFieldCollectionInclude(_tgAppItems, true);
    private void BtnTgExcludeAllApps_Click(object sender, RoutedEventArgs e) => SetFieldCollectionExclude(_tgAppItems, true);
    private void BtnTgClearApps_Click(object sender, RoutedEventArgs e) => ClearFieldCollection(_tgAppItems, true);

    private void BtnTgSelectAllPids_Click(object sender, RoutedEventArgs e) => SetFieldCollectionInclude(_tgPidItems, true);
    private void BtnTgExcludeAllPids_Click(object sender, RoutedEventArgs e) => SetFieldCollectionExclude(_tgPidItems, true);
    private void BtnTgClearPids_Click(object sender, RoutedEventArgs e) => ClearFieldCollection(_tgPidItems, true);

    private void BtnTgSelectAllSpids_Click(object sender, RoutedEventArgs e) => SetFieldCollectionInclude(_tgSpidItems, true);
    private void BtnTgExcludeAllSpids_Click(object sender, RoutedEventArgs e) => SetFieldCollectionExclude(_tgSpidItems, true);
    private void BtnTgClearSpids_Click(object sender, RoutedEventArgs e) => ClearFieldCollection(_tgSpidItems, true);

    private void BtnTgSelectAllThreads_Click(object sender, RoutedEventArgs e) => SetFieldCollectionInclude(_tgThreadItems, true);
    private void BtnTgExcludeAllThreads_Click(object sender, RoutedEventArgs e) => SetFieldCollectionExclude(_tgThreadItems, true);
    private void BtnTgClearThreads_Click(object sender, RoutedEventArgs e) => ClearFieldCollection(_tgThreadItems, true);

    private void SetFieldCollectionInclude(ObservableCollection<FieldValueItem> collection, bool isTg)
    {
        foreach (var item in collection)
        {
            item.IsInclude = true;
            item.IsExclude = false;
        }
        RefreshFieldFilters(isTg);
    }

    private void SetFieldCollectionExclude(ObservableCollection<FieldValueItem> collection, bool isTg)
    {
        foreach (var item in collection)
        {
            item.IsExclude = true;
            item.IsInclude = false;
        }
        RefreshFieldFilters(isTg);
    }

    private void ClearFieldCollection(ObservableCollection<FieldValueItem> collection, bool isTg)
    {
        foreach (var item in collection)
        {
            item.IsInclude = false;
            item.IsExclude = false;
        }
        RefreshFieldFilters(isTg);
    }

    private void RefreshFieldFilters(bool isTg)
    {
        if (isTg)
        {
            UpdateTgFilterChips();
            _tgCollectionView?.Refresh();
            UpdateTgCountStatus();
        }
        else
        {
            UpdateLgFilterChips();
            _lgCollectionView?.Refresh();
            UpdateLgCountStatus();
        }
    }

    #endregion

    private static readonly char[] SearchSeparators = [' ', ',', ';', '\r', '\n', '\t'];

    private static bool MatchSearchQuery(TechLogDoc doc, string query)
    {
        var rawTokens = query.Split(SearchSeparators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawToken in rawTokens)
        {
            var token = rawToken.Trim().Trim(',', ';', '"', '\'');
            if (token.Length == 0) continue;

            bool isNegative = false;
            if (token.StartsWith('!') || token.StartsWith('-'))
            {
                isNegative = true;
                token = token[1..].Trim().Trim(',', ';', '"', '\'');
                if (token.Length == 0) continue; // Пользователь только ввел '!' или '-'
            }
            else if (token.StartsWith("NOT:", StringComparison.OrdinalIgnoreCase) || token.StartsWith("НЕ:", StringComparison.OrdinalIgnoreCase))
            {
                isNegative = true;
                token = token[4..].Trim().Trim(',', ';', '"', '\'');
                if (token.Length == 0) continue;
            }

            bool found = ContainsTgToken(doc, token);
            if (isNegative)
            {
                if (found) return false; // Исключаем строку
            }
            else
            {
                if (!found) return false; // Обязательный токен не найден
            }
        }

        return true;
    }

    private static bool ContainsTgToken(TechLogDoc doc, string token)
    {
        var colonIdx = token.IndexOf(':');
        if (colonIdx > 0 && colonIdx < token.Length - 1)
        {
            var field = token[..colonIdx].ToLowerInvariant();
            var val = token[(colonIdx + 1)..];

            return field switch
            {
                "user" or "usr" or "пользователь" => doc.User != null && doc.User.Contains(val, StringComparison.OrdinalIgnoreCase),
                "event" or "событие" => doc.Event != null && doc.Event.Contains(val, StringComparison.OrdinalIgnoreCase),
                "process" or "процесс" => doc.ProcessName != null && doc.ProcessName.Contains(val, StringComparison.OrdinalIgnoreCase),
                "pid" => doc.ProcessId != null && doc.ProcessId.Contains(val, StringComparison.OrdinalIgnoreCase),
                "spid" => doc.Spid != null && doc.Spid.Contains(val, StringComparison.OrdinalIgnoreCase),
                "thread" or "osthread" or "поток" => doc.OSThread != null && doc.OSThread.Contains(val, StringComparison.OrdinalIgnoreCase),
                "app" or "приложение" => doc.App != null && doc.App.Contains(val, StringComparison.OrdinalIgnoreCase),
                "context" or "контекст" => doc.Context != null && doc.Context.Contains(val, StringComparison.OrdinalIgnoreCase),
                "sql" => doc.Sql != null && doc.Sql.Contains(val, StringComparison.OrdinalIgnoreCase),
                "status" or "статус" => doc.ExecutionStatus.Contains(val, StringComparison.OrdinalIgnoreCase),
                "session" or "сеанс" => doc.SessionId != null && doc.SessionId.Contains(val, StringComparison.OrdinalIgnoreCase),
                _ => ContainsAnyTgField(doc, token)
            };
        }

        return ContainsAnyTgField(doc, token);
    }

    private static bool ContainsAnyTgField(TechLogDoc doc, string token)
    {
        return (!string.IsNullOrEmpty(doc.Event) && doc.Event.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.Spid) && doc.Spid.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.OSThread) && doc.OSThread.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.LongInfoName) && doc.LongInfoName.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.ProcessId) && doc.ProcessId.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.SessionId) && doc.SessionId.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.ConnectId) && doc.ConnectId.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.ClientId) && doc.ClientId.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.ProcessName) && doc.ProcessName.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.User) && doc.User.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.App) && doc.App.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.Context) && doc.Context.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.Sql) && doc.Sql.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.Descr) && doc.Descr.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               doc.ExecutionStatus.Contains(token, StringComparison.OrdinalIgnoreCase) ||
               (doc.Properties.Count > 0 && doc.Properties.Values.Any(v => v.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    private bool FilterTgDoc(object item)
    {
        if (item is not TechLogDoc doc) return false;

        // 1. Поисковый запрос по всем полям с поддержкой отрицаний '!' и '-'
        var search = TxtTgFilter?.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            if (!MatchSearchQuery(doc, search))
                return false;
        }

        // 2. Включающий фильтр событий из ComboBox
        if (CmbTgEventFilter?.SelectedIndex > 0)
        {
            if (CmbTgEventFilter.SelectedItem is ComboBoxItem selectedItem)
            {
                var docEv = doc.Event ?? string.Empty;
                bool matchesEvent;

                if (selectedItem.Tag is string targetEvent)
                {
                    matchesEvent = docEv.Equals(targetEvent, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    var selectedText = selectedItem.Content?.ToString() ?? string.Empty;

                    if (selectedText.Contains("LONGDURATIONINFO", StringComparison.OrdinalIgnoreCase))
                    {
                        matchesEvent = docEv.Equals("LONGDURATIONINFO", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (selectedText.Contains("EXCP", StringComparison.OrdinalIgnoreCase))
                    {
                        matchesEvent = docEv.StartsWith("EXCP", StringComparison.OrdinalIgnoreCase) ||
                                       docEv.Equals("QERR", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (selectedText.Contains("TLOCK", StringComparison.OrdinalIgnoreCase))
                    {
                        matchesEvent = docEv.Contains("TLOCK", StringComparison.OrdinalIgnoreCase) ||
                                       docEv.Contains("TDEADLOCK", StringComparison.OrdinalIgnoreCase) ||
                                       docEv.Contains("TTIMEOUT", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (selectedText.Contains("DBMSSQL", StringComparison.OrdinalIgnoreCase))
                    {
                        matchesEvent = docEv.Contains("DBMSSQL", StringComparison.OrdinalIgnoreCase) ||
                                       docEv.Contains("SDBL", StringComparison.OrdinalIgnoreCase) ||
                                       docEv.Contains("DBPOSTGRS", StringComparison.OrdinalIgnoreCase) ||
                                       docEv.Contains("DBORACLE", StringComparison.OrdinalIgnoreCase) ||
                                       docEv.Contains("DBV8DBENG", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (selectedText.Contains("CALL", StringComparison.OrdinalIgnoreCase))
                    {
                        matchesEvent = docEv.Contains("CALL", StringComparison.OrdinalIgnoreCase) ||
                                       docEv.Contains("SCALL", StringComparison.OrdinalIgnoreCase) ||
                                       docEv.Contains("RUNMETH", StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        matchesEvent = true;
                    }
                }

                if (!matchesEvent)
                    return false;
            }
        }

        // 3. Множественные Исключения по событиям 1С (ListTgExcludeEvents)
        var docEvent = doc.Event ?? string.Empty;
        foreach (var exItem in _tgExcludeEventItems)
        {
            if (exItem.IsChecked && docEvent.Equals(exItem.Tag, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // 4. Включающий фильтр по статусу операции
        bool incRunning = ChkTgIncRunning?.IsChecked == true;
        bool incCompleted = ChkTgIncCompleted?.IsChecked == true;
        if (incRunning && !incCompleted)
        {
            if (!doc.IsActiveOperation) return false;
        }
        else if (!incRunning && incCompleted)
        {
            if (doc.IsActiveOperation) return false;
        }

        // 5. Исключающий фильтр по статусу операции
        if (ChkTgExCompleted?.IsChecked == true && !doc.IsActiveOperation)
            return false;
        if (ChkTgExRunning?.IsChecked == true && doc.IsActiveOperation)
            return false;

        // 6. Исключающий фильтр по процессам (множественный выбор)
        var pName = doc.ProcessName ?? string.Empty;
        if (ChkTgExRphost?.IsChecked == true && pName.Contains("rphost", StringComparison.OrdinalIgnoreCase))
            return false;
        if (ChkTgExRmngr?.IsChecked == true && pName.Contains("rmngr", StringComparison.OrdinalIgnoreCase))
            return false;
        if (ChkTgExRagent?.IsChecked == true && pName.Contains("ragent", StringComparison.OrdinalIgnoreCase))
            return false;

        // 7. Порог длительности (мс)
        if (CmbTgMinDuration?.SelectedIndex > 0)
        {
            var minMs = CmbTgMinDuration.SelectedIndex switch
            {
                1 => 10,
                2 => 100,
                3 => 1000,
                4 => 5000,
                5 => 10000,
                _ => 0
            };
            if (doc.DurationMs < minMs) return false;
        }

        // 8. Фильтр по времени события (С / По)
        if (_tgHasTimeFrom)
        {
            if (_tgExactFrom != default)
            {
                if (doc.Date < _tgExactFrom) return false;
            }
            else
            {
                if (doc.Date.TimeOfDay < _tgTimeFrom) return false;
            }
        }

        if (_tgHasTimeTo)
        {
            if (_tgExactTo != default)
            {
                if (doc.Date > _tgExactTo) return false;
            }
            else
            {
                if (doc.Date.TimeOfDay > _tgTimeTo) return false;
            }
        }

        // 9. Динамический отбор по заполненным полям ТЖ (Пользователи, Приложения, PID, SPID, OSThread)
        if (_tgUserItems.Any(x => x.IsExclude))
        {
            var user = doc.User ?? string.Empty;
            if (_tgUserItems.Any(x => x.IsExclude && string.Equals(x.Value, user, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        if (_tgUserItems.Any(x => x.IsInclude))
        {
            var user = doc.User ?? string.Empty;
            if (!_tgUserItems.Any(x => x.IsInclude && string.Equals(x.Value, user, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        if (_tgAppItems.Any(x => x.IsExclude))
        {
            var app = doc.App ?? string.Empty;
            if (_tgAppItems.Any(x => x.IsExclude && string.Equals(x.Value, app, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        if (_tgAppItems.Any(x => x.IsInclude))
        {
            var app = doc.App ?? string.Empty;
            if (!_tgAppItems.Any(x => x.IsInclude && string.Equals(x.Value, app, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        if (_tgPidItems.Any(x => x.IsExclude))
        {
            var pid = doc.ProcessId ?? string.Empty;
            if (_tgPidItems.Any(x => x.IsExclude && string.Equals(x.Value, pid, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        if (_tgPidItems.Any(x => x.IsInclude))
        {
            var pid = doc.ProcessId ?? string.Empty;
            if (!_tgPidItems.Any(x => x.IsInclude && string.Equals(x.Value, pid, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        if (_tgSpidItems.Any(x => x.IsExclude))
        {
            var spid = doc.Spid ?? string.Empty;
            if (_tgSpidItems.Any(x => x.IsExclude && string.Equals(x.Value, spid, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        if (_tgSpidItems.Any(x => x.IsInclude))
        {
            var spid = doc.Spid ?? string.Empty;
            if (!_tgSpidItems.Any(x => x.IsInclude && string.Equals(x.Value, spid, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        if (_tgThreadItems.Any(x => x.IsExclude))
        {
            var thread = doc.OSThread ?? string.Empty;
            if (_tgThreadItems.Any(x => x.IsExclude && string.Equals(x.Value, thread, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        if (_tgThreadItems.Any(x => x.IsInclude))
        {
            var thread = doc.OSThread ?? string.Empty;
            if (!_tgThreadItems.Any(x => x.IsInclude && string.Equals(x.Value, thread, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        return true;
    }

    private void TxtTgFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateTgFilterChips();
        _tgCollectionView?.Refresh();
        UpdateTgCountStatus();
    }

    private void TxtTgTimeFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        _tgHasTimeFrom = TryParseTimeFilter(TxtTgTimeFrom?.Text, out _tgTimeFrom, out _tgExactFrom);
        _tgHasTimeTo = TryParseTimeFilter(TxtTgTimeTo?.Text, out _tgTimeTo, out _tgExactTo);
        UpdateTgFilterChips();
        _tgCollectionView?.Refresh();
        UpdateTgCountStatus();
    }

    private void CmbTgEventFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateTgFilterChips();
        _tgCollectionView?.Refresh();
        UpdateTgCountStatus();
    }

    private void CmbTgMinDuration_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateTgFilterChips();
        _tgCollectionView?.Refresh();
        UpdateTgCountStatus();
    }

    private void CmbTgSortPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyTgSortPreset();
    }

    private void ApplyTgSortPreset()
    {
        _activeTgSorts.Clear();

        if (CmbTgSortPreset?.SelectedItem is ComboBoxItem item)
        {
            var text = item.Content?.ToString() ?? string.Empty;
            if (text.Contains("сначала новые"))
            {
                _activeTgSorts.Add(("Date", ListSortDirection.Descending));
            }
            else if (text.Contains("сначала старые"))
            {
                _activeTgSorts.Add(("Date", ListSortDirection.Ascending));
            }
            else if (text.Contains("сначала долгие"))
            {
                _activeTgSorts.Add(("DurationMs", ListSortDirection.Descending));
            }
            else if (text.Contains("Длительность + Новые дата"))
            {
                _activeTgSorts.Add(("DurationMs", ListSortDirection.Descending));
                _activeTgSorts.Add(("Date", ListSortDirection.Descending));
            }
            else if (text.Contains("Событие + Длительность"))
            {
                _activeTgSorts.Add(("Event", ListSortDirection.Ascending));
                _activeTgSorts.Add(("DurationMs", ListSortDirection.Descending));
            }
            else if (text.Contains("Пользователь + Дата"))
            {
                _activeTgSorts.Add(("User", ListSortDirection.Ascending));
                _activeTgSorts.Add(("Date", ListSortDirection.Descending));
            }
            else
            {
                _activeTgSorts.Add(("Date", ListSortDirection.Descending));
            }
        }
        else
        {
            _activeTgSorts.Add(("Date", ListSortDirection.Descending));
        }

        if (_techLogDocs != null && _techLogDocs.Count > 0)
        {
            SortTechLogDocs(_techLogDocs, _activeTgSorts);
        }

        if (GridTg?.Columns != null)
        {
            foreach (var col in GridTg.Columns)
            {
                var match = _activeTgSorts.FirstOrDefault(s => string.Equals(s.SortMemberPath, col.SortMemberPath, StringComparison.OrdinalIgnoreCase));
                col.SortDirection = match != default ? match.Direction : null;
            }
        }

        _tgCollectionView?.Refresh();
    }

    private void BtnResetTgFilters_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            this,
            "Вы действительно хотите полностью очистить таблицу Технологического Журнала?",
            "Очистка таблицы ТЖ",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        if (TxtTgFilter != null) TxtTgFilter.Text = string.Empty;
        if (TxtTgTimeFrom != null) TxtTgTimeFrom.Text = string.Empty;
        if (TxtTgTimeTo != null) TxtTgTimeTo.Text = string.Empty;
        _tgHasTimeFrom = false;
        _tgHasTimeTo = false;
        if (CmbTgEventFilter != null) CmbTgEventFilter.SelectedIndex = 0;
        foreach (var item in _tgExcludeEventItems) item.IsChecked = false;
        if (ChkTgIncRunning != null) ChkTgIncRunning.IsChecked = false;
        if (ChkTgIncCompleted != null) ChkTgIncCompleted.IsChecked = false;
        if (ChkTgExRphost != null) ChkTgExRphost.IsChecked = false;
        if (ChkTgExRmngr != null) ChkTgExRmngr.IsChecked = false;
        if (ChkTgExRagent != null) ChkTgExRagent.IsChecked = false;
        if (ChkTgExCompleted != null) ChkTgExCompleted.IsChecked = false;
        if (ChkTgExRunning != null) ChkTgExRunning.IsChecked = false;
        if (CmbTgMinDuration != null) CmbTgMinDuration.SelectedIndex = 0;
        if (CmbTgSortPreset != null) CmbTgSortPreset.SelectedIndex = 0;

        ClearFieldCollection(_tgUserItems, false);
        ClearFieldCollection(_tgAppItems, false);
        ClearFieldCollection(_tgPidItems, false);
        ClearFieldCollection(_tgSpidItems, false);
        ClearFieldCollection(_tgThreadItems, false);

        if (BtnTgMoreChips != null) BtnTgMoreChips.IsChecked = false;

        _techLogDocs.Clear();
        _activeTgSorts.Clear();
        if (GridTg?.Columns != null)
        {
            foreach (var col in GridTg.Columns) col.SortDirection = null;
        }

        UpdateTgFilterChips();
        _tgCollectionView?.Refresh();
        UpdateTgCountStatus();

        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);

        TxtStatus.Text = "Таблица Технологического Журнала очищена.";
    }

    private void UpdateTgCountStatus()
    {
        if (TxtTgCountBadge == null) return;
        var total = _techLogDocs.Count;
        var count = _tgCollectionView?.Cast<object>().Count() ?? total;
        TxtTgCountBadge.Text = $"{count:N0} записей";
    }

    #region Фильтрация и Поиск ЖР (EventLog)

    /// <summary>
    /// Справочник описаний для известных системных событий Журнала Регистрации 1С.
    /// </summary>
    private static readonly Dictionary<string, string> KnownLgEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["_$Data$_.Post"] = "Проведение документа",
        ["_$Data$_.Unpost"] = "Отмена проведения",
        ["_$Data$_.New"] = "Создание нового объекта",
        ["_$Data$_.Update"] = "Изменение данных объекта",
        ["_$Data$_.Delete"] = "Удаление объекта базы",
        ["_$Data$_.TotalsRecalculation"] = "Пересчет итогов регистров",
        ["_$Session$_.Start"] = "Начало сеанса пользователя",
        ["_$Session$_.Finish"] = "Завершение сеанса",
        ["_$Session$_.Authentication"] = "Успешная аутентификация",
        ["_$Session$_.AuthenticationError"] = "Ошибка аутентификации",
        ["_$User$_.New"] = "Создание пользователя",
        ["_$User$_.Update"] = "Изменение пользователя",
        ["_$User$_.Delete"] = "Удаление пользователя",
        ["_$Job$_.Start"] = "Старт фонового задания",
        ["_$Job$_.Finish"] = "Успех фонового задания",
        ["_$Job$_.Fail"] = "Ошибка фонового задания",
        ["_$Access$_.AccessDenied"] = "Отказ в доступе по RLS",
        ["_$Transaction$_.Commit"] = "Фиксация транзакции",
        ["_$Transaction$_.Rollback"] = "Откат транзакции",
        ["_$InfoBase$_.ConfigUpdate"] = "Обновление конфигурации БД"
    };

    private void PopulateLgEventFilterItems()
    {
        if (CmbLgImportanceFilter == null) return;

        CmbLgImportanceFilter.SelectionChanged -= CmbLgImportanceFilter_SelectionChanged;

        var selectedText = (CmbLgImportanceFilter.SelectedItem as ComboBoxItem)?.Content?.ToString()
            ?? (CmbLgImportanceFilter.SelectedItem as string);
        var selectedTag = (CmbLgImportanceFilter.SelectedItem as ComboBoxItem)?.Tag as string;

        CmbLgImportanceFilter.Items.Clear();
        CmbLgImportanceFilter.Items.Add(new ComboBoxItem { Content = "Все уровни и события" });
        CmbLgImportanceFilter.Items.Add(new ComboBoxItem { Content = "Ошибка" });
        CmbLgImportanceFilter.Items.Add(new ComboBoxItem { Content = "Предупреждение" });
        CmbLgImportanceFilter.Items.Add(new ComboBoxItem { Content = "Информация" });
        CmbLgImportanceFilter.Items.Add(new ComboBoxItem { Content = "Примечание" });

        if (_eventLogDocs.Count == 0)
        {
            // Логи еще не загружены — показываем полный справочник системных событий ЖР
            foreach (var (ev, desc) in KnownLgEvents.OrderBy(k => k.Key))
            {
                CmbLgImportanceFilter.Items.Add(new ComboBoxItem
                {
                    Content = $"{ev} — {desc}",
                    Tag = ev
                });
            }
        }
        else
        {
            var eventCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var doc in _eventLogDocs)
            {
                if (string.IsNullOrWhiteSpace(doc.Event)) continue;
                eventCounts[doc.Event] = eventCounts.GetValueOrDefault(doc.Event, 0) + 1;
            }

            // 1. Сначала события, найденные в текущем ЖР
            var presentEvents = eventCounts.Keys.OrderBy(e => e).ToList();
            foreach (var ev in presentEvents)
            {
                var count = eventCounts[ev];
                string itemContent = KnownLgEvents.TryGetValue(ev, out var desc)
                    ? $"{ev} — {desc} ({count:N0})"
                    : $"{ev} ({count:N0})";

                CmbLgImportanceFilter.Items.Add(new ComboBoxItem { Content = itemContent, Tag = ev });
            }

            // 2. Затем остальные события из справочника (со счетчиком 0)
            var absentKnown = KnownLgEvents.Keys
                .Where(k => !eventCounts.ContainsKey(k))
                .OrderBy(k => k);

            foreach (var ev in absentKnown)
            {
                var desc = KnownLgEvents[ev];
                CmbLgImportanceFilter.Items.Add(new ComboBoxItem
                {
                    Content = $"{ev} — {desc} (0)",
                    Tag = ev
                });
            }
        }

        int initialIndex = CmbLgImportanceFilter.SelectedIndex;
        int targetIndex = 0;
        if (!string.IsNullOrEmpty(selectedTag))
        {
            for (int i = 0; i < CmbLgImportanceFilter.Items.Count; i++)
            {
                if (CmbLgImportanceFilter.Items[i] is ComboBoxItem item && string.Equals(item.Tag as string, selectedTag, StringComparison.OrdinalIgnoreCase))
                {
                    targetIndex = i;
                    break;
                }
            }
        }

        if (targetIndex == 0 && !string.IsNullOrEmpty(selectedText))
        {
            for (int i = 0; i < CmbLgImportanceFilter.Items.Count; i++)
            {
                if (CmbLgImportanceFilter.Items[i] is ComboBoxItem item && string.Equals(item.Content?.ToString(), selectedText, StringComparison.OrdinalIgnoreCase))
                {
                    targetIndex = i;
                    break;
                }
            }
        }

        if (targetIndex == 0 && initialIndex > 0 && initialIndex < 5)
        {
            targetIndex = initialIndex;
        }

        CmbLgImportanceFilter.SelectedIndex = targetIndex;
        CmbLgImportanceFilter.SelectionChanged += CmbLgImportanceFilter_SelectionChanged;
    }

    private void PopulateLgExcludeFilterItems()
    {
        if (ListLgExcludeEvents == null) return;

        var previouslyChecked = _lgExcludeEventItems.Where(x => x.IsChecked).Select(x => x.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _lgExcludeEventItems.Clear();

        foreach (var (ev, desc) in KnownLgEvents.OrderBy(k => k.Key))
        {
            _lgExcludeEventItems.Add(new FilterCheckItem
            {
                Tag = ev,
                Title = ev,
                Subtitle = desc,
                IsChecked = previouslyChecked.Contains(ev)
            });
        }

        FilterLgExcludeEventList();
    }

    private void TxtLgExcludeEventSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterLgExcludeEventList();
    }

    private void FilterLgExcludeEventList()
    {
        if (ListLgExcludeEvents == null) return;
        var search = TxtLgExcludeEventSearch?.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(search))
        {
            ListLgExcludeEvents.ItemsSource = _lgExcludeEventItems;
        }
        else
        {
            ListLgExcludeEvents.ItemsSource = _lgExcludeEventItems.Where(x =>
                x.Tag.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                x.Subtitle.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    private void BtnLgExcludeClearAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _lgExcludeEventItems)
        {
            item.IsChecked = false;
        }
        UpdateLgFilterChips();
        _lgCollectionView?.Refresh();
        UpdateLgCountStatus();
    }

    private void ChkLgExcludeEventItem_Click(object sender, RoutedEventArgs e)
    {
        UpdateLgFilterChips();
        _lgCollectionView?.Refresh();
        UpdateLgCountStatus();
    }

    private void ChkLgFilter_Click(object sender, RoutedEventArgs e)
    {
        UpdateLgFilterChips();
        _lgCollectionView?.Refresh();
        UpdateLgCountStatus();
    }

    #region Отбор по полям ЖР (Пользователи, Приложения, Метаданные, События)

    public void PopulateLgFieldValues()
    {
        if (_eventLogDocs.Count == 0) return;

        PopulateFieldList(_eventLogDocs.Select(x => x.User ?? ""), _lgUserItems, "Пользователь", ListLgUsers);
        PopulateFieldList(_eventLogDocs.Select(x => x.App ?? ""), _lgAppItems, "Приложение", ListLgApps);
        PopulateFieldList(_eventLogDocs.Select(x => x.Meta ?? ""), _lgMetaItems, "Метаданные", ListLgMetas);
        PopulateFieldList(_eventLogDocs.Select(x => x.Event ?? ""), _lgEventFieldItems, "Событие", ListLgEvents);
    }

    private void ChkLgFieldValueItem_Click(object sender, RoutedEventArgs e)
    {
        UpdateLgFilterChips();
        _lgCollectionView?.Refresh();
        UpdateLgCountStatus();
    }

    private void BtnLgClearAllFieldFilters_Click(object sender, RoutedEventArgs e)
    {
        ClearFieldCollection(_lgUserItems, false);
        ClearFieldCollection(_lgAppItems, false);
        ClearFieldCollection(_lgMetaItems, false);
        ClearFieldCollection(_lgEventFieldItems, false);
    }

    private void TxtLgSearchUsers_TextChanged(object sender, TextChangedEventArgs e) => FilterFieldControl(TxtLgSearchUsers.Text, _lgUserItems, ListLgUsers);
    private void TxtLgSearchApps_TextChanged(object sender, TextChangedEventArgs e) => FilterFieldControl(TxtLgSearchApps.Text, _lgAppItems, ListLgApps);
    private void TxtLgSearchMetas_TextChanged(object sender, TextChangedEventArgs e) => FilterFieldControl(TxtLgSearchMetas.Text, _lgMetaItems, ListLgMetas);
    private void TxtLgSearchEvents_TextChanged(object sender, TextChangedEventArgs e) => FilterFieldControl(TxtLgSearchEvents.Text, _lgEventFieldItems, ListLgEvents);

    private void BtnLgSelectAllUsers_Click(object sender, RoutedEventArgs e) => SetFieldCollectionInclude(_lgUserItems, false);
    private void BtnLgExcludeAllUsers_Click(object sender, RoutedEventArgs e) => SetFieldCollectionExclude(_lgUserItems, false);
    private void BtnLgClearUsers_Click(object sender, RoutedEventArgs e) => ClearFieldCollection(_lgUserItems, false);

    private void BtnLgSelectAllApps_Click(object sender, RoutedEventArgs e) => SetFieldCollectionInclude(_lgAppItems, false);
    private void BtnLgExcludeAllApps_Click(object sender, RoutedEventArgs e) => SetFieldCollectionExclude(_lgAppItems, false);
    private void BtnLgClearApps_Click(object sender, RoutedEventArgs e) => ClearFieldCollection(_lgAppItems, false);

    private void BtnLgSelectAllMetas_Click(object sender, RoutedEventArgs e) => SetFieldCollectionInclude(_lgMetaItems, false);
    private void BtnLgExcludeAllMetas_Click(object sender, RoutedEventArgs e) => SetFieldCollectionExclude(_lgMetaItems, false);
    private void BtnLgClearMetas_Click(object sender, RoutedEventArgs e) => ClearFieldCollection(_lgMetaItems, false);

    private void BtnLgSelectAllEvents_Click(object sender, RoutedEventArgs e) => SetFieldCollectionInclude(_lgEventFieldItems, false);
    private void BtnLgExcludeAllEvents_Click(object sender, RoutedEventArgs e) => SetFieldCollectionExclude(_lgEventFieldItems, false);
    private void BtnLgClearEvents_Click(object sender, RoutedEventArgs e) => ClearFieldCollection(_lgEventFieldItems, false);

    #endregion

    private static bool MatchLgSearchQuery(EventLogDoc doc, string query)
    {
        var rawTokens = query.Split(SearchSeparators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawToken in rawTokens)
        {
            var token = rawToken.Trim().Trim(',', ';', '"', '\'');
            if (token.Length == 0) continue;

            bool isNegative = false;
            if (token.StartsWith('!') || token.StartsWith('-'))
            {
                isNegative = true;
                token = token[1..].Trim().Trim(',', ';', '"', '\'');
                if (token.Length == 0) continue;
            }
            else if (token.StartsWith("NOT:", StringComparison.OrdinalIgnoreCase) || token.StartsWith("НЕ:", StringComparison.OrdinalIgnoreCase))
            {
                isNegative = true;
                token = token[4..].Trim().Trim(',', ';', '"', '\'');
                if (token.Length == 0) continue;
            }

            bool found = ContainsLgToken(doc, token);
            if (isNegative)
            {
                if (found) return false;
            }
            else
            {
                if (!found) return false;
            }
        }

        return true;
    }

    private static bool ContainsLgToken(EventLogDoc doc, string token)
    {
        var colonIdx = token.IndexOf(':');
        if (colonIdx > 0 && colonIdx < token.Length - 1)
        {
            var field = token[..colonIdx].ToLowerInvariant();
            var val = token[(colonIdx + 1)..];

            return field switch
            {
                "user" or "usr" or "пользователь" => doc.User != null && doc.User.Contains(val, StringComparison.OrdinalIgnoreCase),
                "event" or "событие" => doc.Event != null && doc.Event.Contains(val, StringComparison.OrdinalIgnoreCase),
                "level" or "важность" or "importance" => doc.Importance != null && doc.Importance.Contains(val, StringComparison.OrdinalIgnoreCase),
                "app" or "приложение" => doc.App != null && doc.App.Contains(val, StringComparison.OrdinalIgnoreCase),
                "meta" or "мета" or "метаданные" => doc.Meta != null && doc.Meta.Contains(val, StringComparison.OrdinalIgnoreCase),
                "comment" or "комментарий" => doc.Comment != null && doc.Comment.Contains(val, StringComparison.OrdinalIgnoreCase),
                _ => ContainsAnyLgField(doc, val)
            };
        }

        return ContainsAnyLgField(doc, token);
    }

    private static bool ContainsAnyLgField(EventLogDoc doc, string token)
    {
        return (!string.IsNullOrEmpty(doc.Event) && doc.Event.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.Importance) && doc.Importance.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.User) && doc.User.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.App) && doc.App.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.Meta) && doc.Meta.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.Comment) && doc.Comment.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private bool FilterLgDoc(object item)
    {
        if (item is not EventLogDoc doc) return false;

        // 1. Поисковый запрос с поддержкой отрицаний '!' и '-'
        var search = TxtLgFilter?.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            if (!MatchLgSearchQuery(doc, search))
                return false;
        }

        // 2. Включающий фильтр важности/события из ComboBox
        if (CmbLgImportanceFilter?.SelectedIndex > 0)
        {
            if (CmbLgImportanceFilter.SelectedItem is ComboBoxItem selectedItem)
            {
                var docImp = doc.Importance ?? string.Empty;
                var docEv = doc.Event ?? string.Empty;
                bool matches;

                if (selectedItem.Tag is string targetEvent)
                {
                    matches = docEv.Equals(targetEvent, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    var selectedText = selectedItem.Content?.ToString() ?? string.Empty;

                    if (selectedText.Contains("Ошибка", StringComparison.OrdinalIgnoreCase))
                    {
                        matches = docImp.Contains("Ошибка", StringComparison.OrdinalIgnoreCase) ||
                                  docImp.Contains("Error", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (selectedText.Contains("Предупреждение", StringComparison.OrdinalIgnoreCase))
                    {
                        matches = docImp.Contains("Предупреждение", StringComparison.OrdinalIgnoreCase) ||
                                  docImp.Contains("Warn", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (selectedText.Contains("Информация", StringComparison.OrdinalIgnoreCase))
                    {
                        matches = docImp.Contains("Информация", StringComparison.OrdinalIgnoreCase) ||
                                  docImp.Contains("Info", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (selectedText.Contains("Примечание", StringComparison.OrdinalIgnoreCase))
                    {
                        matches = docImp.Contains("Примечание", StringComparison.OrdinalIgnoreCase) ||
                                  docImp.Contains("Note", StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        matches = true;
                    }
                }

                if (!matches)
                    return false;
            }
        }

        var docImportance = doc.Importance ?? string.Empty;
        var docEvent = doc.Event ?? string.Empty;

        // 3. Включающие чекбоксы уровней важности (множественный выбор)
        bool hasIncLevel = ChkLgIncError?.IsChecked == true || ChkLgIncWarn?.IsChecked == true ||
                           ChkLgIncInfo?.IsChecked == true || ChkLgIncNote?.IsChecked == true;
        if (hasIncLevel)
        {
            bool matchLevel = false;
            if (ChkLgIncError?.IsChecked == true && (docImportance.Contains("Ошибка", StringComparison.OrdinalIgnoreCase) || docImportance.Contains("Error", StringComparison.OrdinalIgnoreCase))) matchLevel = true;
            if (ChkLgIncWarn?.IsChecked == true && (docImportance.Contains("Предупреждение", StringComparison.OrdinalIgnoreCase) || docImportance.Contains("Warn", StringComparison.OrdinalIgnoreCase))) matchLevel = true;
            if (ChkLgIncInfo?.IsChecked == true && (docImportance.Contains("Информация", StringComparison.OrdinalIgnoreCase) || docImportance.Contains("Info", StringComparison.OrdinalIgnoreCase))) matchLevel = true;
            if (ChkLgIncNote?.IsChecked == true && (docImportance.Contains("Примечание", StringComparison.OrdinalIgnoreCase) || docImportance.Contains("Note", StringComparison.OrdinalIgnoreCase))) matchLevel = true;
            if (!matchLevel) return false;
        }

        // 4. Исключающие чекбоксы уровней важности (множественный выбор)
        if (ChkLgExError?.IsChecked == true && (docImportance.Contains("Ошибка", StringComparison.OrdinalIgnoreCase) || docImportance.Contains("Error", StringComparison.OrdinalIgnoreCase))) return false;
        if (ChkLgExWarn?.IsChecked == true && (docImportance.Contains("Предупреждение", StringComparison.OrdinalIgnoreCase) || docImportance.Contains("Warn", StringComparison.OrdinalIgnoreCase))) return false;
        if (ChkLgExInfo?.IsChecked == true && (docImportance.Contains("Информация", StringComparison.OrdinalIgnoreCase) || docImportance.Contains("Info", StringComparison.OrdinalIgnoreCase))) return false;
        if (ChkLgExNote?.IsChecked == true && (docImportance.Contains("Примечание", StringComparison.OrdinalIgnoreCase) || docImportance.Contains("Note", StringComparison.OrdinalIgnoreCase))) return false;

        // 5. Исключающие события ЖР
        foreach (var exItem in _lgExcludeEventItems)
        {
            if (exItem.IsChecked && docEvent.Equals(exItem.Tag, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // 6. Фильтр по времени события (С / По)
        if (_lgHasTimeFrom)
        {
            if (_lgExactFrom != default)
            {
                if (doc.Date < _lgExactFrom) return false;
            }
            else
            {
                if (doc.Date.TimeOfDay < _lgTimeFrom) return false;
            }
        }

        if (_lgHasTimeTo)
        {
            if (_lgExactTo != default)
            {
                if (doc.Date > _lgExactTo) return false;
            }
            else
            {
                if (doc.Date.TimeOfDay > _lgTimeTo) return false;
            }
        }

        // 7. Динамический отбор по заполненным полям ЖР (Пользователи, Приложения, Метаданные, События)
        if (_lgUserItems.Any(x => x.IsExclude))
        {
            var user = doc.User ?? string.Empty;
            if (_lgUserItems.Any(x => x.IsExclude && string.Equals(x.Value, user, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        if (_lgUserItems.Any(x => x.IsInclude))
        {
            var user = doc.User ?? string.Empty;
            if (!_lgUserItems.Any(x => x.IsInclude && string.Equals(x.Value, user, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        if (_lgAppItems.Any(x => x.IsExclude))
        {
            var app = doc.App ?? string.Empty;
            if (_lgAppItems.Any(x => x.IsExclude && string.Equals(x.Value, app, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        if (_lgAppItems.Any(x => x.IsInclude))
        {
            var app = doc.App ?? string.Empty;
            if (!_lgAppItems.Any(x => x.IsInclude && string.Equals(x.Value, app, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        if (_lgMetaItems.Any(x => x.IsExclude))
        {
            var meta = doc.Meta ?? string.Empty;
            if (_lgMetaItems.Any(x => x.IsExclude && string.Equals(x.Value, meta, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        if (_lgMetaItems.Any(x => x.IsInclude))
        {
            var meta = doc.Meta ?? string.Empty;
            if (!_lgMetaItems.Any(x => x.IsInclude && string.Equals(x.Value, meta, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        if (_lgEventFieldItems.Any(x => x.IsExclude))
        {
            var ev = doc.Event ?? string.Empty;
            if (_lgEventFieldItems.Any(x => x.IsExclude && string.Equals(x.Value, ev, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        if (_lgEventFieldItems.Any(x => x.IsInclude))
        {
            var ev = doc.Event ?? string.Empty;
            if (!_lgEventFieldItems.Any(x => x.IsInclude && string.Equals(x.Value, ev, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        return true;
    }

    private void TxtLgFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateLgFilterChips();
        _lgCollectionView?.Refresh();
        UpdateLgCountStatus();
    }

    private void TxtLgTimeFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        _lgHasTimeFrom = TryParseTimeFilter(TxtLgTimeFrom?.Text, out _lgTimeFrom, out _lgExactFrom);
        _lgHasTimeTo = TryParseTimeFilter(TxtLgTimeTo?.Text, out _lgTimeTo, out _lgExactTo);
        UpdateLgFilterChips();
        _lgCollectionView?.Refresh();
        UpdateLgCountStatus();
    }

    private void CmbLgImportanceFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateLgFilterChips();
        _lgCollectionView?.Refresh();
        UpdateLgCountStatus();
    }

    private void CmbLgSortPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyLgSortPreset();
    }

    private void ApplyLgSortPreset()
    {
        _activeLgSorts.Clear();

        if (CmbLgSortPreset?.SelectedItem is ComboBoxItem item)
        {
            var text = item.Content?.ToString() ?? string.Empty;
            if (text.Contains("сначала новые"))
            {
                _activeLgSorts.Add(("Date", ListSortDirection.Descending));
            }
            else if (text.Contains("сначала старые"))
            {
                _activeLgSorts.Add(("Date", ListSortDirection.Ascending));
            }
            else if (text.Contains("Важность + Дата"))
            {
                _activeLgSorts.Add(("Importance", ListSortDirection.Ascending));
                _activeLgSorts.Add(("Date", ListSortDirection.Descending));
            }
            else if (text.Contains("Событие + Дата"))
            {
                _activeLgSorts.Add(("Event", ListSortDirection.Ascending));
                _activeLgSorts.Add(("Date", ListSortDirection.Descending));
            }
            else if (text.Contains("Пользователь + Дата"))
            {
                _activeLgSorts.Add(("User", ListSortDirection.Ascending));
                _activeLgSorts.Add(("Date", ListSortDirection.Descending));
            }
            else
            {
                _activeLgSorts.Add(("Date", ListSortDirection.Descending));
            }
        }
        else
        {
            _activeLgSorts.Add(("Date", ListSortDirection.Descending));
        }

        if (_eventLogDocs != null && _eventLogDocs.Count > 0)
        {
            SortEventLogDocs(_eventLogDocs, _activeLgSorts);
        }

        if (GridLg?.Columns != null)
        {
            foreach (var col in GridLg.Columns)
            {
                var match = _activeLgSorts.FirstOrDefault(s => string.Equals(s.SortMemberPath, col.SortMemberPath, StringComparison.OrdinalIgnoreCase));
                col.SortDirection = match != default ? match.Direction : null;
            }
        }

        _lgCollectionView?.Refresh();
    }

    private void BtnResetLgFilters_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            this,
            "Вы действительно хотите полностью очистить таблицу Журнала Регистрации?",
            "Очистка таблицы ЖР",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        if (TxtLgFilter != null) TxtLgFilter.Text = string.Empty;
        if (TxtLgTimeFrom != null) TxtLgTimeFrom.Text = string.Empty;
        if (TxtLgTimeTo != null) TxtLgTimeTo.Text = string.Empty;
        _lgHasTimeFrom = false;
        _lgHasTimeTo = false;
        if (CmbLgImportanceFilter != null) CmbLgImportanceFilter.SelectedIndex = 0;
        foreach (var item in _lgExcludeEventItems) item.IsChecked = false;
        if (ChkLgIncError != null) ChkLgIncError.IsChecked = false;
        if (ChkLgIncWarn != null) ChkLgIncWarn.IsChecked = false;
        if (ChkLgIncInfo != null) ChkLgIncInfo.IsChecked = false;
        if (ChkLgIncNote != null) ChkLgIncNote.IsChecked = false;
        if (ChkLgExError != null) ChkLgExError.IsChecked = false;
        if (ChkLgExWarn != null) ChkLgExWarn.IsChecked = false;
        if (ChkLgExInfo != null) ChkLgExInfo.IsChecked = false;
        if (ChkLgExNote != null) ChkLgExNote.IsChecked = false;
        if (CmbLgSortPreset != null) CmbLgSortPreset.SelectedIndex = 0;

        ClearFieldCollection(_lgUserItems, false);
        ClearFieldCollection(_lgAppItems, false);
        ClearFieldCollection(_lgMetaItems, false);
        ClearFieldCollection(_lgEventFieldItems, false);

        if (BtnLgMoreChips != null) BtnLgMoreChips.IsChecked = false;

        _eventLogDocs.Clear();
        _activeLgSorts.Clear();
        if (GridLg?.Columns != null)
        {
            foreach (var col in GridLg.Columns) col.SortDirection = null;
        }

        UpdateLgFilterChips();
        _lgCollectionView?.Refresh();
        UpdateLgCountStatus();

        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);

        TxtStatus.Text = "Таблица Журнала Регистрации очищена.";
    }

    /// <summary>
    /// Универсальный разбор строки времени/даты для гибкой фильтрации.
    /// Поддерживает: "16:13:00", "16:13:50.875", "16:13", "2026-08-17 16:13:00", "17.08.2026 16:13".
    /// </summary>
    private static bool TryParseTimeFilter(string? input, out TimeSpan timeOfDay, out DateTime exactDate)
    {
        timeOfDay = default;
        exactDate = default;
        if (string.IsNullOrWhiteSpace(input)) return false;

        input = input.Trim();

        // 1. Полноформатная дата и время
        if (DateTime.TryParse(input, out exactDate))
        {
            timeOfDay = exactDate.TimeOfDay;
            return true;
        }

        // 2. Время суток (HH:mm:ss.fff / HH:mm:ss / HH:mm)
        if (TimeSpan.TryParse(input, out timeOfDay))
        {
            return true;
        }

        // 3. Формат без двоеточий или с точкой, например "16.13.00"
        var normalized = input.Replace('.', ':');
        if (TimeSpan.TryParse(normalized, out timeOfDay))
        {
            return true;
        }

        return false;
    }

    private void UpdateLgCountStatus()
    {
        if (TxtLgCountBadge == null) return;
        var total = _eventLogDocs.Count;
        var count = _lgCollectionView?.Cast<object>().Count() ?? total;
        TxtLgCountBadge.Text = $"{count:N0} записей";
    }

    #endregion

    #region Мульти-Сортировка (Multi-Column Sorting)

    private async void GridTg_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        await HandleMultiColumnSortingAsync(GridTg, _tgCollectionView, e.Column);
    }

    private async void GridLg_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        await HandleMultiColumnSortingAsync(GridLg, _lgCollectionView, e.Column);
    }

    private async Task HandleMultiColumnSortingAsync(DataGrid dataGrid, ICollectionView? collectionView, DataGridColumn column)
    {
        if (collectionView == null || string.IsNullOrEmpty(column.SortMemberPath)) return;

        var headerText = column.Header?.ToString() ?? column.SortMemberPath;
        var sortMemberPath = column.SortMemberPath;
        var newDirection = column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

        var dirText = newDirection == ListSortDirection.Ascending ? "по возрастанию ▲" : "по убыванию ▼";
        TxtStatus.Text = $"⏳ Выполняется сортировка таблицы по колонке '{headerText}' ({dirText})...";
        Mouse.OverrideCursor = Cursors.Wait;

        try
        {
            // Отдаем квант времени для мгновенной отрисовки статуса и курсора ожидания
            await Task.Yield();

            bool isShiftPressed = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            var isTg = ReferenceEquals(dataGrid, GridTg);
            var activeSorts = isTg ? _activeTgSorts : _activeLgSorts;

            if (!isShiftPressed)
            {
                foreach (var col in dataGrid.Columns)
                {
                    if (col != column) col.SortDirection = null;
                }
                activeSorts.Clear();
            }
            else
            {
                activeSorts.RemoveAll(s => string.Equals(s.SortMemberPath, sortMemberPath, StringComparison.OrdinalIgnoreCase));
            }

            column.SortDirection = newDirection;
            activeSorts.Insert(0, (sortMemberPath, newDirection));

            if (isTg)
            {
                SortTechLogDocs(_techLogDocs, activeSorts);
            }
            else
            {
                SortEventLogDocs(_eventLogDocs, activeSorts);
            }

            collectionView.SortDescriptions.Clear();
            collectionView.Refresh();

            TxtStatus.Text = $"Сортировка завершена: '{headerText}' ({dirText}).";
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private static void SortTechLogDocs(List<TechLogDoc> docs, IReadOnlyList<(string SortMemberPath, ListSortDirection Direction)> sorts)
    {
        if (docs.Count <= 1) return;
        if (sorts.Count == 0)
        {
            docs.Sort((a, b) => b.Date.CompareTo(a.Date));
            return;
        }

        docs.Sort((a, b) =>
        {
            foreach (var (prop, dir) in sorts)
            {
                int cmp = prop switch
                {
                    "Date" => a.Date.CompareTo(b.Date),
                    "DurationMs" or "Duration" => a.Duration.CompareTo(b.Duration),
                    "Event" => string.Compare(a.Event, b.Event, StringComparison.OrdinalIgnoreCase),
                    "IsActiveOperation" => a.IsActiveOperation.CompareTo(b.IsActiveOperation),
                    "ProcessName" => string.Compare(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase),
                    "ProcessId" => string.Compare(a.ProcessId, b.ProcessId, StringComparison.OrdinalIgnoreCase),
                    "Spid" => string.Compare(a.Spid, b.Spid, StringComparison.OrdinalIgnoreCase),
                    "OSThread" => string.Compare(a.OSThread, b.OSThread, StringComparison.OrdinalIgnoreCase),
                    "User" => string.Compare(a.User, b.User, StringComparison.OrdinalIgnoreCase),
                    "App" => string.Compare(a.App, b.App, StringComparison.OrdinalIgnoreCase),
                    "Context" => string.Compare(a.Context, b.Context, StringComparison.OrdinalIgnoreCase),
                    _ => 0
                };

                if (cmp != 0)
                {
                    return dir == ListSortDirection.Ascending ? cmp : -cmp;
                }
            }
            return b.Date.CompareTo(a.Date);
        });
    }

    private static void SortEventLogDocs(List<EventLogDoc> docs, IReadOnlyList<(string SortMemberPath, ListSortDirection Direction)> sorts)
    {
        if (docs.Count <= 1) return;
        if (sorts.Count == 0)
        {
            docs.Sort((a, b) => b.Date.CompareTo(a.Date));
            return;
        }

        docs.Sort((a, b) =>
        {
            foreach (var (prop, dir) in sorts)
            {
                int cmp = prop switch
                {
                    "Date" => a.Date.CompareTo(b.Date),
                    "Event" => string.Compare(a.Event, b.Event, StringComparison.OrdinalIgnoreCase),
                    "User" => string.Compare(a.User, b.User, StringComparison.OrdinalIgnoreCase),
                    "App" => string.Compare(a.App, b.App, StringComparison.OrdinalIgnoreCase),
                    "Meta" => string.Compare(a.Meta, b.Meta, StringComparison.OrdinalIgnoreCase),
                    "Tran" => string.Compare(a.Tran, b.Tran, StringComparison.OrdinalIgnoreCase),
                    "Comment" => string.Compare(a.Comment, b.Comment, StringComparison.OrdinalIgnoreCase),
                    "Importance" => string.Compare(a.Importance, b.Importance, StringComparison.OrdinalIgnoreCase),
                    "Data" => string.Compare(a.Data, b.Data, StringComparison.OrdinalIgnoreCase),
                    "Session" => string.Compare(a.Session, b.Session, StringComparison.OrdinalIgnoreCase),
                    _ => 0
                };

                if (cmp != 0)
                {
                    return dir == ListSortDirection.Ascending ? cmp : -cmp;
                }
            }
            return b.Date.CompareTo(a.Date);
        });
    }

    #endregion

    #region Копирование ячеек, строк и JSON через контекстное меню (ПКМ) и Ctrl+C

    private string _lastClickedTgCellText = "";
    private TechLogDoc? _lastClickedTgDoc;
    private string _lastClickedLgCellText = "";
    private EventLogDoc? _lastClickedLgDoc;

    private void GridTg_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep)
        {
            var cell = FindVisualParent<DataGridCell>(dep);
            if (cell != null)
            {
                if (cell.Content is TextBlock tb)
                {
                    _lastClickedTgCellText = tb.Text;
                }
                else if (cell.Content is ContentPresenter cp && cp.Content != null)
                {
                    _lastClickedTgCellText = cp.Content.ToString() ?? "";
                }
            }

            var row = FindVisualParent<DataGridRow>(dep);
            if (row != null && row.Item is TechLogDoc doc)
            {
                _lastClickedTgDoc = doc;
                if (!GridTg.SelectedItems.Contains(doc))
                {
                    GridTg.SelectedItem = doc;
                }
            }
        }
    }

    private void GridTg_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            CopyTgSelectionToClipboard();
            e.Handled = true;
        }
    }

    private void TgMenuCopyCell_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastClickedTgCellText))
        {
            Clipboard.SetText(_lastClickedTgCellText);
            TxtStatus.Text = $"📋 Скопировано значение ячейки: {_lastClickedTgCellText}";
        }
        else if (GridTg.SelectedItem is TechLogDoc doc)
        {
            Clipboard.SetText(doc.Context ?? doc.Event ?? "");
            TxtStatus.Text = "📋 Скопировано содержимое записи.";
        }
    }

    private void TgMenuCopyRowText_Click(object sender, RoutedEventArgs e)
    {
        CopyTgSelectionToClipboard();
    }

    private void TgMenuCopyRowJson_Click(object sender, RoutedEventArgs e)
    {
        var items = GridTg.SelectedItems.Cast<TechLogDoc>().ToList();
        if (items.Count == 0 && _lastClickedTgDoc != null) items.Add(_lastClickedTgDoc);
        if (items.Count == 0) return;

        var json = items.Count == 1
            ? JsonSerializer.Serialize(items[0], PrettyJson)
            : JsonSerializer.Serialize(items, PrettyJson);
        Clipboard.SetText(json);
        TxtStatus.Text = $"📦 Скопировано {items.Count} записей ТЖ в формате JSON.";
    }

    private void TgMenuCopyContextSql_Click(object sender, RoutedEventArgs e)
    {
        var doc = _lastClickedTgDoc ?? GridTg.SelectedItem as TechLogDoc;
        if (doc == null) return;
        var text = !string.IsNullOrWhiteSpace(doc.Context) ? doc.Context : doc.Sql ?? doc.Descr ?? "";
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
            TxtStatus.Text = "🔍 Скопирован контекст / SQL запрос записи.";
        }
    }

    private void TgMenuInspectRow_Click(object sender, RoutedEventArgs e)
    {
        var doc = _lastClickedTgDoc ?? GridTg.SelectedItem as TechLogDoc;
        if (doc != null)
        {
            ShowDocDetails(doc);
        }
    }

    private void CopyTgSelectionToClipboard()
    {
        var items = GridTg.SelectedItems.Cast<TechLogDoc>().ToList();
        if (items.Count == 0 && _lastClickedTgDoc != null) items.Add(_lastClickedTgDoc);
        if (items.Count == 0) return;

        var sb = new StringBuilder();
        foreach (var doc in items)
        {
            sb.AppendLine($"{doc.DateFormatted}\t{doc.Event}\t{doc.ExecutionStatus}\t{doc.DurationFormatted}\t{doc.ProcessName}\t{doc.ProcessId}\t{doc.Spid}\t{doc.OSThread}\t{doc.User}\t{doc.App}\t{doc.Context}");
        }
        Clipboard.SetText(sb.ToString().TrimEnd());
        TxtStatus.Text = $"📑 Скопировано {items.Count} строк ТЖ в буфер обмена.";
    }

    private void GridLg_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep)
        {
            var cell = FindVisualParent<DataGridCell>(dep);
            if (cell != null)
            {
                if (cell.Content is TextBlock tb)
                {
                    _lastClickedLgCellText = tb.Text;
                }
                else if (cell.Content is ContentPresenter cp && cp.Content != null)
                {
                    _lastClickedLgCellText = cp.Content.ToString() ?? "";
                }
            }

            var row = FindVisualParent<DataGridRow>(dep);
            if (row != null && row.Item is EventLogDoc doc)
            {
                _lastClickedLgDoc = doc;
                if (!GridLg.SelectedItems.Contains(doc))
                {
                    GridLg.SelectedItem = doc;
                }
            }
        }
    }

    private void GridLg_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            CopyLgSelectionToClipboard();
            e.Handled = true;
        }
    }

    private void LgMenuCopyCell_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastClickedLgCellText))
        {
            Clipboard.SetText(_lastClickedLgCellText);
            TxtStatus.Text = $"📋 Скопировано значение ячейки: {_lastClickedLgCellText}";
        }
        else if (GridLg.SelectedItem is EventLogDoc doc)
        {
            Clipboard.SetText(doc.Comment ?? doc.Event ?? "");
            TxtStatus.Text = "📋 Скопировано содержимое записи ЖР.";
        }
    }

    private void LgMenuCopyRowText_Click(object sender, RoutedEventArgs e)
    {
        CopyLgSelectionToClipboard();
    }

    private void LgMenuCopyRowJson_Click(object sender, RoutedEventArgs e)
    {
        var items = GridLg.SelectedItems.Cast<EventLogDoc>().ToList();
        if (items.Count == 0 && _lastClickedLgDoc != null) items.Add(_lastClickedLgDoc);
        if (items.Count == 0) return;

        var json = items.Count == 1
            ? JsonSerializer.Serialize(items[0], PrettyJson)
            : JsonSerializer.Serialize(items, PrettyJson);
        Clipboard.SetText(json);
        TxtStatus.Text = $"📦 Скопировано {items.Count} записей ЖР в формате JSON.";
    }

    private void LgMenuCopyComment_Click(object sender, RoutedEventArgs e)
    {
        var doc = _lastClickedLgDoc ?? GridLg.SelectedItem as EventLogDoc;
        if (doc == null) return;
        var text = doc.Comment ?? doc.Meta ?? "";
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
            TxtStatus.Text = "💬 Скопирован комментарий записи ЖР.";
        }
    }

    private void LgMenuInspectRow_Click(object sender, RoutedEventArgs e)
    {
        var doc = _lastClickedLgDoc ?? GridLg.SelectedItem as EventLogDoc;
        if (doc != null)
        {
            ShowDocDetails(doc);
        }
    }

    private void CopyLgSelectionToClipboard()
    {
        var items = GridLg.SelectedItems.Cast<EventLogDoc>().ToList();
        if (items.Count == 0 && _lastClickedLgDoc != null) items.Add(_lastClickedLgDoc);
        if (items.Count == 0) return;

        var sb = new StringBuilder();
        foreach (var doc in items)
        {
            sb.AppendLine($"{doc.DateFormatted}\t{doc.Event}\t{doc.Importance}\t{doc.User}\t{doc.App}\t{doc.Meta}\t{doc.Comment}");
        }
        Clipboard.SetText(sb.ToString().TrimEnd());
        TxtStatus.Text = $"📑 Скопировано {items.Count} строк ЖР в буфер обмена.";
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parentObject = VisualTreeHelper.GetParent(child);
        if (parentObject == null) return null;
        if (parentObject is T parent) return parent;
        return FindVisualParent<T>(parentObject);
    }

    #endregion

    #region Активные Фильтры (Чипы / Filter Badges)

    private readonly struct FilterChipModel
    {
        public string Text { get; }
        public bool IsNegative { get; }
        public Action OnRemove { get; }

        public FilterChipModel(string text, bool isNegative, Action onRemove)
        {
            Text = text;
            IsNegative = isNegative;
            OnRemove = onRemove;
        }
    }

    private void UpdateTgFilterChips()
    {
        if (ContainerTgFilterChips == null) return;
        var chips = new List<FilterChipModel>();

        var query = TxtTgFilter?.Text ?? "";
        var rawTokens = query.Split(SearchSeparators, StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawToken in rawTokens)
        {
            var token = rawToken.Trim().Trim(',', ';', '"', '\'');
            if (token.Length == 0) continue;

            bool isNegative = token.StartsWith('!') || token.StartsWith('-') ||
                              token.StartsWith("NOT:", StringComparison.OrdinalIgnoreCase) ||
                              token.StartsWith("НЕ:", StringComparison.OrdinalIgnoreCase);

            chips.Add(new FilterChipModel(
                text: isNegative ? $"НЕ: {token}" : token,
                isNegative: isNegative,
                onRemove: () =>
                {
                    RemoveSearchToken(TxtTgFilter, rawToken);
                }));
        }

        // Фильтр времени
        if (_tgHasTimeFrom || _tgHasTimeTo)
        {
            var from = TxtTgTimeFrom?.Text?.Trim() ?? "";
            var to = TxtTgTimeTo?.Text?.Trim() ?? "";
            var timeDesc = !string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(to)
                ? $"{from} — {to}"
                : !string.IsNullOrEmpty(from) ? $"с {from}" : $"по {to}";

            chips.Add(new FilterChipModel(timeDesc, isNegative: false, onRemove: () =>
            {
                if (TxtTgTimeFrom != null) TxtTgTimeFrom.Text = "";
                if (TxtTgTimeTo != null) TxtTgTimeTo.Text = "";
            }));
        }

        // Включающее событие (ComboBox)
        if (CmbTgEventFilter?.SelectedIndex > 0)
        {
            var selectedText = (CmbTgEventFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            var tag = (CmbTgEventFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? selectedText;
            var shortLabel = tag.Length > 25 ? tag[..25] + "..." : tag;
            chips.Add(new FilterChipModel(shortLabel, isNegative: false, onRemove: () =>
            {
                CmbTgEventFilter.SelectedIndex = 0;
            }));
        }

        // Включающие статусы (чекбоксы)
        if (ChkTgIncRunning?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("Выполняются", isNegative: false, onRemove: () =>
            {
                ChkTgIncRunning.IsChecked = false;
                ChkTgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        if (ChkTgIncCompleted?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("Завершенные", isNegative: false, onRemove: () =>
            {
                ChkTgIncCompleted.IsChecked = false;
                ChkTgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        // Мин длительность
        if (CmbTgMinDuration?.SelectedIndex > 0)
        {
            var durText = (CmbTgMinDuration.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            chips.Add(new FilterChipModel(durText, isNegative: false, onRemove: () =>
            {
                CmbTgMinDuration.SelectedIndex = 0;
            }));
        }

        // Исключающие события (множественный выбор чекбоксами)
        foreach (var exItem in _tgExcludeEventItems.Where(x => x.IsChecked))
        {
            var target = exItem;
            chips.Add(new FilterChipModel($"НЕ: {target.Tag}", isNegative: true, onRemove: () =>
            {
                target.IsChecked = false;
                ChkTgExcludeEventItem_Click(this, new RoutedEventArgs());
            }));
        }

        // Исключающие процессы (чекбоксы)
        if (ChkTgExRphost?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("НЕ: rphost", isNegative: true, onRemove: () =>
            {
                ChkTgExRphost.IsChecked = false;
                ChkTgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        if (ChkTgExRmngr?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("НЕ: rmngr", isNegative: true, onRemove: () =>
            {
                ChkTgExRmngr.IsChecked = false;
                ChkTgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        if (ChkTgExRagent?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("НЕ: ragent", isNegative: true, onRemove: () =>
            {
                ChkTgExRagent.IsChecked = false;
                ChkTgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        // Исключающие статусы
        if (ChkTgExCompleted?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("НЕ: Завершенные", isNegative: true, onRemove: () =>
            {
                ChkTgExCompleted.IsChecked = false;
                ChkTgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        if (ChkTgExRunning?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("НЕ: Выполняющиеся", isNegative: true, onRemove: () =>
            {
                ChkTgExRunning.IsChecked = false;
                ChkTgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        // Чипы выбранных полей ТЖ (Пользователи, Приложения, PID, SPID, OSThread)
        CollectFieldFilterChips(_tgUserItems, chips, true);
        CollectFieldFilterChips(_tgAppItems, chips, true);
        CollectFieldFilterChips(_tgPidItems, chips, true);
        CollectFieldFilterChips(_tgSpidItems, chips, true);
        CollectFieldFilterChips(_tgThreadItems, chips, true);

        RenderFilterChips(chips, PanelTgPopupChips, BtnTgMoreChips, ContainerTgFilterChips);
    }

    private void CollectFieldFilterChips(ObservableCollection<FieldValueItem> collection, List<FilterChipModel> chips, bool isTg)
    {
        foreach (var item in collection.Where(x => x.IsInclude || x.IsExclude))
        {
            var target = item;
            var label = string.IsNullOrEmpty(target.Value) ? "<Пусто>" : target.Value;
            var shortLabel = label.Length > 22 ? label[..22] + "..." : label;
            bool isEx = target.IsExclude;
            var chipText = isEx ? $"НЕ {target.Category}: {shortLabel}" : $"{target.Category}: {shortLabel}";

            chips.Add(new FilterChipModel(chipText, isNegative: isEx, onRemove: () =>
            {
                target.IsInclude = false;
                target.IsExclude = false;
                if (isTg)
                {
                    UpdateTgFilterChips();
                    _tgCollectionView?.Refresh();
                    UpdateTgCountStatus();
                }
                else
                {
                    UpdateLgFilterChips();
                    _lgCollectionView?.Refresh();
                    UpdateLgCountStatus();
                }
            }));
        }
    }

    private void UpdateLgFilterChips()
    {
        if (ContainerLgFilterChips == null) return;
        var chips = new List<FilterChipModel>();

        var query = TxtLgFilter?.Text ?? "";
        var rawTokens = query.Split(SearchSeparators, StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawToken in rawTokens)
        {
            var token = rawToken.Trim().Trim(',', ';', '"', '\'');
            if (token.Length == 0) continue;

            bool isNegative = token.StartsWith('!') || token.StartsWith('-') ||
                              token.StartsWith("NOT:", StringComparison.OrdinalIgnoreCase) ||
                              token.StartsWith("НЕ:", StringComparison.OrdinalIgnoreCase);

            chips.Add(new FilterChipModel(
                text: isNegative ? $"НЕ: {token}" : token,
                isNegative: isNegative,
                onRemove: () =>
                {
                    RemoveSearchToken(TxtLgFilter, rawToken);
                }));
        }

        if (_lgHasTimeFrom || _lgHasTimeTo)
        {
            var from = TxtLgTimeFrom?.Text?.Trim() ?? "";
            var to = TxtLgTimeTo?.Text?.Trim() ?? "";
            var timeDesc = !string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(to)
                ? $"{from} — {to}"
                : !string.IsNullOrEmpty(from) ? $"с {from}" : $"по {to}";

            chips.Add(new FilterChipModel(timeDesc, isNegative: false, onRemove: () =>
            {
                if (TxtLgTimeFrom != null) TxtLgTimeFrom.Text = "";
                if (TxtLgTimeTo != null) TxtLgTimeTo.Text = "";
            }));
        }

        if (CmbLgImportanceFilter?.SelectedIndex > 0)
        {
            var selectedText = (CmbLgImportanceFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            var tag = (CmbLgImportanceFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? selectedText;
            var shortLabel = tag.Length > 25 ? tag[..25] + "..." : tag;
            chips.Add(new FilterChipModel(shortLabel, isNegative: false, onRemove: () =>
            {
                CmbLgImportanceFilter.SelectedIndex = 0;
            }));
        }

        // Включающие уровни важности ЖР
        if (ChkLgIncError?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("Ошибки", isNegative: false, onRemove: () =>
            {
                ChkLgIncError.IsChecked = false;
                ChkLgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        if (ChkLgIncWarn?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("Предупреждения", isNegative: false, onRemove: () =>
            {
                ChkLgIncWarn.IsChecked = false;
                ChkLgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        if (ChkLgIncInfo?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("Информация", isNegative: false, onRemove: () =>
            {
                ChkLgIncInfo.IsChecked = false;
                ChkLgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        if (ChkLgIncNote?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("Примечания", isNegative: false, onRemove: () =>
            {
                ChkLgIncNote.IsChecked = false;
                ChkLgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        // Исключающие уровни важности ЖР
        if (ChkLgExError?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("НЕ: Ошибки", isNegative: true, onRemove: () =>
            {
                ChkLgExError.IsChecked = false;
                ChkLgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        if (ChkLgExWarn?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("НЕ: Предупреждения", isNegative: true, onRemove: () =>
            {
                ChkLgExWarn.IsChecked = false;
                ChkLgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        if (ChkLgExInfo?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("НЕ: Информация", isNegative: true, onRemove: () =>
            {
                ChkLgExInfo.IsChecked = false;
                ChkLgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        if (ChkLgExNote?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("НЕ: Примечания", isNegative: true, onRemove: () =>
            {
                ChkLgExNote.IsChecked = false;
                ChkLgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        // Исключающие события ЖР
        foreach (var exItem in _lgExcludeEventItems.Where(x => x.IsChecked))
        {
            var target = exItem;
            chips.Add(new FilterChipModel($"НЕ: {target.Tag}", isNegative: true, onRemove: () =>
            {
                target.IsChecked = false;
                ChkLgExcludeEventItem_Click(this, new RoutedEventArgs());
            }));
        }

        // Чипы выбранных полей ЖР (Пользователи, Приложения, Метаданные, События)
        CollectFieldFilterChips(_lgUserItems, chips, false);
        CollectFieldFilterChips(_lgAppItems, chips, false);
        CollectFieldFilterChips(_lgMetaItems, chips, false);
        CollectFieldFilterChips(_lgEventFieldItems, chips, false);

        RenderFilterChips(chips, PanelLgPopupChips, BtnLgMoreChips, ContainerLgFilterChips);
    }

    private static void RenderFilterChips(
        List<FilterChipModel> chips,
        Panel? popupPanel,
        ToggleButton? moreButton,
        UIElement? container)
    {
        if (container == null || popupPanel == null || moreButton == null) return;

        popupPanel.Children.Clear();

        if (chips.Count == 0)
        {
            container.Visibility = Visibility.Collapsed;
            moreButton.IsChecked = false;
            return;
        }

        container.Visibility = Visibility.Visible;
        moreButton.Content = $"АКТИВНЫЕ ФИЛЬТРЫ ({chips.Count}) ▼";

        foreach (var item in chips)
        {
            popupPanel.Children.Add(CreateFilterChip(item.Text, item.IsNegative, item.OnRemove));
        }
    }

    private static Border CreateFilterChip(string text, bool isNegative, Action onRemove)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(15),
            Background = new SolidColorBrush(isNegative ? Color.FromArgb(0x2E, 0x8A, 0x2B, 0x2B) : Color.FromArgb(0x22, 0xE6, 0x6F, 0x27)),
            BorderBrush = new SolidColorBrush(isNegative ? Color.FromRgb(0x8A, 0x2B, 0x2B) : Color.FromRgb(0xE6, 0x6F, 0x27)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(9, 2, 7, 2),
            Margin = new Thickness(0, 0, 6, 4),
            Cursor = Cursors.Hand
        };

        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var tb = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(isNegative ? Color.FromRgb(0xFA, 0xCA, 0xCA) : Color.FromRgb(0xFF, 0xFF, 0xFF)),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };

        var btnRemove = new Button
        {
            Content = "✕",
            Foreground = new SolidColorBrush(isNegative ? Color.FromRgb(0xEF, 0x44, 0x44) : Color.FromRgb(0xE6, 0x6F, 0x27)),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(2, 0, 2, 0),
            Cursor = Cursors.Hand,
            ToolTip = "Удалить фильтр"
        };
        btnRemove.Click += (_, _) => onRemove();

        sp.Children.Add(tb);
        sp.Children.Add(btnRemove);
        border.Child = sp;

        return border;
    }

    private static void RemoveSearchToken(TextBox? textBox, string tokenToRemove)
    {
        if (textBox == null || string.IsNullOrWhiteSpace(textBox.Text)) return;
        var text = textBox.Text;
        var idx = text.IndexOf(tokenToRemove, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            text = text.Remove(idx, tokenToRemove.Length).Trim();
            text = text.Replace(", ,", ",").Trim(',', ' ');
            textBox.Text = text;
        }
    }

    #endregion

    #region Экспорт в Microsoft Excel (.xlsx)

    private async void BtnExportTgExcel_Click(object sender, RoutedEventArgs e)
    {
        await ExportTgDocsToExcelAsync();
    }

    private async void TgMenuExportExcel_Click(object sender, RoutedEventArgs e)
    {
        await ExportTgDocsToExcelAsync();
    }

    private async void BtnExportLgExcel_Click(object sender, RoutedEventArgs e)
    {
        await ExportLgDocsToExcelAsync();
    }

    private async void LgMenuExportExcel_Click(object sender, RoutedEventArgs e)
    {
        await ExportLgDocsToExcelAsync();
    }

    private async Task ExportTgDocsToExcelAsync()
    {
        var selectedDocs = GridTg.SelectedItems.OfType<TechLogDoc>().ToList();
        var isSelected = selectedDocs.Count > 0;
        var docsToExport = isSelected
            ? selectedDocs
            : (_tgCollectionView?.Cast<TechLogDoc>().ToList() ?? _techLogDocs);

        if (docsToExport.Count == 0)
        {
            MessageBox.Show("Нет данных Технологического Журнала (ТЖ) для экспорта в Excel! Сначала распарсьте логи или выберите строки.", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var scopePrefix = isSelected ? $"selected_{docsToExport.Count}" : "table";
        var saveDialog = new SaveFileDialog
        {
            Title = isSelected ? $"Экспорт {docsToExport.Count} выделенных строк ТЖ в Excel" : $"Экспорт таблицы ТЖ ({docsToExport.Count} строк) в Excel",
            Filter = "Книга Microsoft Excel (*.xlsx)|*.xlsx|Все файлы (*.*)|*.*",
            FileName = $"ones_techlog_{scopePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
        };

        if (saveDialog.ShowDialog() == true)
        {
            TxtStatus.Text = $"⏳ Формирование файла Excel (.xlsx) для {docsToExport.Count} записей ТЖ...";
            Mouse.OverrideCursor = Cursors.Wait;

            try
            {
                var filePath = saveDialog.FileName;
                await Task.Run(() => ExcelExportService.ExportTechLogToExcel(filePath, docsToExport));

                var scopeText = isSelected ? $"выделенные {docsToExport.Count} строк(и)" : $"все {docsToExport.Count} строк(и) таблицы";
                TxtStatus.Text = $"📊 Экспортировано {docsToExport.Count} записей ТЖ в Excel: {Path.GetFileName(filePath)}";

                var openResult = MessageBox.Show(
                    $"Экспорт ТЖ в Microsoft Excel успешно завершен!\n\n• Сохранены: {scopeText}\n• Файл: {filePath}\n\nОткрыть созданный файл в Excel?",
                    "Экспорт в Excel",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (openResult == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo { FileName = filePath, UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при формировании Excel файла ТЖ:\n{ex.Message}", "Ошибка экспорта", MessageBoxButton.OK, MessageBoxImage.Error);
                TxtStatus.Text = "Ошибка экспорта в Excel.";
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }
    }

    private async Task ExportLgDocsToExcelAsync()
    {
        var selectedDocs = GridLg.SelectedItems.OfType<EventLogDoc>().ToList();
        var isSelected = selectedDocs.Count > 0;
        var docsToExport = isSelected
            ? selectedDocs
            : (_lgCollectionView?.Cast<EventLogDoc>().ToList() ?? _eventLogDocs);

        if (docsToExport.Count == 0)
        {
            MessageBox.Show("Нет данных Журнала Регистрации (ЖР) для экспорта в Excel! Сначала распарсьте логи или выберите строки.", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var scopePrefix = isSelected ? $"selected_{docsToExport.Count}" : "table";
        var saveDialog = new SaveFileDialog
        {
            Title = isSelected ? $"Экспорт {docsToExport.Count} выделенных строк ЖР в Excel" : $"Экспорт таблицы ЖР ({docsToExport.Count} строк) в Excel",
            Filter = "Книга Microsoft Excel (*.xlsx)|*.xlsx|Все файлы (*.*)|*.*",
            FileName = $"ones_eventlog_{scopePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
        };

        if (saveDialog.ShowDialog() == true)
        {
            TxtStatus.Text = $"⏳ Формирование файла Excel (.xlsx) для {docsToExport.Count} записей ЖР...";
            Mouse.OverrideCursor = Cursors.Wait;

            try
            {
                var filePath = saveDialog.FileName;
                await Task.Run(() => ExcelExportService.ExportEventLogToExcel(filePath, docsToExport));

                var scopeText = isSelected ? $"выделенные {docsToExport.Count} строк(и)" : $"все {docsToExport.Count} строк(и) таблицы";
                TxtStatus.Text = $"📊 Экспортировано {docsToExport.Count} записей ЖР в Excel: {Path.GetFileName(filePath)}";

                var openResult = MessageBox.Show(
                    $"Экспорт ЖР в Microsoft Excel успешно завершен!\n\n• Сохранены: {scopeText}\n• Файл: {filePath}\n\nОткрыть созданный файл в Excel?",
                    "Экспорт в Excel",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (openResult == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo { FileName = filePath, UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при формировании Excel файла ЖР:\n{ex.Message}", "Ошибка экспорта", MessageBoxButton.OK, MessageBoxImage.Error);
                TxtStatus.Text = "Ошибка экспорта в Excel.";
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }
    }

    #endregion

    #region Персистентность состояния GUI (ui_state.json)

    private static string GetGuiStateFilePath()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui_state.json");
    }

    private void LoadGuiState()
    {
        try
        {
            var path = GetGuiStateFilePath();
            if (!File.Exists(path)) return;

            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<GuiState>(json);
            if (state == null) return;

            if (!string.IsNullOrWhiteSpace(state.TgPath)) TxtTgPath.Text = state.TgPath;
            if (!string.IsNullOrWhiteSpace(state.LgPath)) TxtLgPath.Text = state.LgPath;

            if (state.TgFilter != null) TxtTgFilter.Text = state.TgFilter;
            if (state.TgTimeFrom != null) TxtTgTimeFrom.Text = state.TgTimeFrom;
            if (state.TgTimeTo != null) TxtTgTimeTo.Text = state.TgTimeTo;
            if (state.TgEventIndex >= 0 && state.TgEventIndex < CmbTgEventFilter.Items.Count)
                CmbTgEventFilter.SelectedIndex = state.TgEventIndex;

            if (state.TgExcludeEvents != null && state.TgExcludeEvents.Count > 0)
            {
                var set = state.TgExcludeEvents.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var item in _tgExcludeEventItems)
                {
                    item.IsChecked = set.Contains(item.Tag);
                }
            }

            if (ChkTgIncRunning != null) ChkTgIncRunning.IsChecked = state.TgIncRunning;
            if (ChkTgIncCompleted != null) ChkTgIncCompleted.IsChecked = state.TgIncCompleted;

            if (ChkTgExRphost != null) ChkTgExRphost.IsChecked = state.TgExRphost;
            if (ChkTgExRmngr != null) ChkTgExRmngr.IsChecked = state.TgExRmngr;
            if (ChkTgExRagent != null) ChkTgExRagent.IsChecked = state.TgExRagent;

            if (ChkTgExCompleted != null) ChkTgExCompleted.IsChecked = state.TgExCompleted;
            if (ChkTgExRunning != null) ChkTgExRunning.IsChecked = state.TgExRunning;

            if (state.TgMinDurationIndex >= 0 && state.TgMinDurationIndex < CmbTgMinDuration.Items.Count)
                CmbTgMinDuration.SelectedIndex = state.TgMinDurationIndex;
            if (state.TgSortIndex >= 0 && state.TgSortIndex < CmbTgSortPreset.Items.Count)
                CmbTgSortPreset.SelectedIndex = state.TgSortIndex;
            if (state.TgLimitIndex >= 0 && state.TgLimitIndex < CmbTgLimit.Items.Count)
                CmbTgLimit.SelectedIndex = state.TgLimitIndex;

            RestoreFieldSelections(state.TgIncludedUsers, state.TgExcludedUsers, _tgUserItems);
            RestoreFieldSelections(state.TgIncludedApps, state.TgExcludedApps, _tgAppItems);
            RestoreFieldSelections(state.TgIncludedPids, state.TgExcludedPids, _tgPidItems);
            RestoreFieldSelections(state.TgIncludedSpids, state.TgExcludedSpids, _tgSpidItems);
            RestoreFieldSelections(state.TgIncludedThreads, state.TgExcludedThreads, _tgThreadItems);

            if (state.LgFilter != null) TxtLgFilter.Text = state.LgFilter;
            if (state.LgTimeFrom != null) TxtLgTimeFrom.Text = state.LgTimeFrom;
            if (state.LgTimeTo != null) TxtLgTimeTo.Text = state.LgTimeTo;
            if (state.LgImportanceIndex >= 0 && state.LgImportanceIndex < CmbLgImportanceFilter.Items.Count)
                CmbLgImportanceFilter.SelectedIndex = state.LgImportanceIndex;

            if (ChkLgIncError != null) ChkLgIncError.IsChecked = state.LgIncError;
            if (ChkLgIncWarn != null) ChkLgIncWarn.IsChecked = state.LgIncWarn;
            if (ChkLgIncInfo != null) ChkLgIncInfo.IsChecked = state.LgIncInfo;
            if (ChkLgIncNote != null) ChkLgIncNote.IsChecked = state.LgIncNote;

            if (ChkLgExError != null) ChkLgExError.IsChecked = state.LgExError;
            if (ChkLgExWarn != null) ChkLgExWarn.IsChecked = state.LgExWarn;
            if (ChkLgExInfo != null) ChkLgExInfo.IsChecked = state.LgExInfo;
            if (ChkLgExNote != null) ChkLgExNote.IsChecked = state.LgExNote;

            if (state.LgExcludeEvents != null && state.LgExcludeEvents.Count > 0)
            {
                var set = state.LgExcludeEvents.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var item in _lgExcludeEventItems)
                {
                    item.IsChecked = set.Contains(item.Tag);
                }
            }

            if (state.LgSortIndex >= 0 && state.LgSortIndex < CmbLgSortPreset.Items.Count)
                CmbLgSortPreset.SelectedIndex = state.LgSortIndex;
            if (state.LgLimitIndex >= 0 && state.LgLimitIndex < CmbLgLimit.Items.Count)
                CmbLgLimit.SelectedIndex = state.LgLimitIndex;

            RestoreFieldSelections(state.LgIncludedUsers, state.LgExcludedUsers, _lgUserItems);
            RestoreFieldSelections(state.LgIncludedApps, state.LgExcludedApps, _lgAppItems);
            RestoreFieldSelections(state.LgIncludedMetas, state.LgExcludedMetas, _lgMetaItems);
            RestoreFieldSelections(state.LgIncludedEvents, state.LgExcludedEvents, _lgEventFieldItems);

            if (state.SelectedTabIndex >= 0 && state.SelectedTabIndex < MainTabControl.Items.Count)
                MainTabControl.SelectedIndex = state.SelectedTabIndex;
        }
        catch
        {
            // Устойчивость при ошибках чтения поврежденного JSON
        }
    }

    private static void RestoreFieldSelections(List<string>? incValues, List<string>? exValues, ObservableCollection<FieldValueItem> collection)
    {
        var incSet = incValues != null && incValues.Count > 0 ? incValues.ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        var exSet = exValues != null && exValues.Count > 0 ? exValues.ToHashSet(StringComparer.OrdinalIgnoreCase) : null;

        foreach (var item in collection)
        {
            if (incSet != null && incSet.Contains(item.Value))
            {
                item.IsInclude = true;
                item.IsExclude = false;
            }
            else if (exSet != null && exSet.Contains(item.Value))
            {
                item.IsExclude = true;
                item.IsInclude = false;
            }
        }
    }

    private void SaveGuiState()
    {
        try
        {
            var state = new GuiState
            {
                TgPath = TxtTgPath.Text,
                TgFilter = TxtTgFilter.Text,
                TgTimeFrom = TxtTgTimeFrom.Text,
                TgTimeTo = TxtTgTimeTo.Text,
                TgEventIndex = CmbTgEventFilter.SelectedIndex,
                TgExcludeEvents = _tgExcludeEventItems.Where(x => x.IsChecked).Select(x => x.Tag).ToList(),
                TgIncRunning = ChkTgIncRunning?.IsChecked == true,
                TgIncCompleted = ChkTgIncCompleted?.IsChecked == true,
                TgExRphost = ChkTgExRphost?.IsChecked == true,
                TgExRmngr = ChkTgExRmngr?.IsChecked == true,
                TgExRagent = ChkTgExRagent?.IsChecked == true,
                TgExCompleted = ChkTgExCompleted?.IsChecked == true,
                TgExRunning = ChkTgExRunning?.IsChecked == true,
                TgMinDurationIndex = CmbTgMinDuration.SelectedIndex,
                TgSortIndex = CmbTgSortPreset.SelectedIndex,
                TgLimitIndex = CmbTgLimit.SelectedIndex,
                TgIncludedUsers = _tgUserItems.Where(x => x.IsInclude).Select(x => x.Value).ToList(),
                TgExcludedUsers = _tgUserItems.Where(x => x.IsExclude).Select(x => x.Value).ToList(),
                TgIncludedApps = _tgAppItems.Where(x => x.IsInclude).Select(x => x.Value).ToList(),
                TgExcludedApps = _tgAppItems.Where(x => x.IsExclude).Select(x => x.Value).ToList(),
                TgIncludedPids = _tgPidItems.Where(x => x.IsInclude).Select(x => x.Value).ToList(),
                TgExcludedPids = _tgPidItems.Where(x => x.IsExclude).Select(x => x.Value).ToList(),
                TgIncludedSpids = _tgSpidItems.Where(x => x.IsInclude).Select(x => x.Value).ToList(),
                TgExcludedSpids = _tgSpidItems.Where(x => x.IsExclude).Select(x => x.Value).ToList(),
                TgIncludedThreads = _tgThreadItems.Where(x => x.IsInclude).Select(x => x.Value).ToList(),
                TgExcludedThreads = _tgThreadItems.Where(x => x.IsExclude).Select(x => x.Value).ToList(),

                LgPath = TxtLgPath.Text,
                LgFilter = TxtLgFilter.Text,
                LgTimeFrom = TxtLgTimeFrom.Text,
                LgTimeTo = TxtLgTimeTo.Text,
                LgImportanceIndex = CmbLgImportanceFilter.SelectedIndex,
                LgIncError = ChkLgIncError?.IsChecked == true,
                LgIncWarn = ChkLgIncWarn?.IsChecked == true,
                LgIncInfo = ChkLgIncInfo?.IsChecked == true,
                LgIncNote = ChkLgIncNote?.IsChecked == true,
                LgExError = ChkLgExError?.IsChecked == true,
                LgExWarn = ChkLgExWarn?.IsChecked == true,
                LgExInfo = ChkLgExInfo?.IsChecked == true,
                LgExNote = ChkLgExNote?.IsChecked == true,
                LgExcludeEvents = _lgExcludeEventItems.Where(x => x.IsChecked).Select(x => x.Tag).ToList(),
                LgSortIndex = CmbLgSortPreset.SelectedIndex,
                LgLimitIndex = CmbLgLimit.SelectedIndex,
                LgIncludedUsers = _lgUserItems.Where(x => x.IsInclude).Select(x => x.Value).ToList(),
                LgExcludedUsers = _lgUserItems.Where(x => x.IsExclude).Select(x => x.Value).ToList(),
                LgIncludedApps = _lgAppItems.Where(x => x.IsInclude).Select(x => x.Value).ToList(),
                LgExcludedApps = _lgAppItems.Where(x => x.IsExclude).Select(x => x.Value).ToList(),
                LgIncludedMetas = _lgMetaItems.Where(x => x.IsInclude).Select(x => x.Value).ToList(),
                LgExcludedMetas = _lgMetaItems.Where(x => x.IsExclude).Select(x => x.Value).ToList(),
                LgIncludedEvents = _lgEventFieldItems.Where(x => x.IsInclude).Select(x => x.Value).ToList(),
                LgExcludedEvents = _lgEventFieldItems.Where(x => x.IsExclude).Select(x => x.Value).ToList(),

                SelectedTabIndex = MainTabControl.SelectedIndex
            };

            var json = JsonSerializer.Serialize(state, PrettyJson);
            File.WriteAllText(GetGuiStateFilePath(), json);
        }
        catch
        {
            // Защита от исключений при закрытии приложения
        }
    }

    #endregion
}

/// <summary>
/// Элемент отбора по конкретному значению поля таблицы (User, App, PID, SPID, OSThread, Meta, Event).
/// Поддерживает независимое включение (IsInclude) и исключение (IsExclude / НЕ).
/// </summary>
public sealed class FieldValueItem : INotifyPropertyChanged
{
    private bool _isInclude;
    private bool _isExclude;

    public string Category { get; set; } = "";
    public string Value { get; set; } = "";
    public int Count { get; set; }
    public string DisplayText => string.IsNullOrEmpty(Value) ? $"<Пусто> ({Count:N0})" : $"{Value} ({Count:N0})";

    public bool IsInclude
    {
        get => _isInclude;
        set
        {
            if (_isInclude != value)
            {
                _isInclude = value;
                if (value) _isExclude = false;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInclude)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExclude)));
            }
        }
    }

    public bool IsExclude
    {
        get => _isExclude;
        set
        {
            if (_isExclude != value)
            {
                _isExclude = value;
                if (value) _isInclude = false;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInclude)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExclude)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Элемент списка фильтра с поддержкой множественного выбора через CheckBox.
/// </summary>
public sealed class FilterCheckItem : INotifyPropertyChanged
{
    private bool _isChecked;
    public string Tag { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked != value)
            {
                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Модель сохраняемого локального состояния пользовательского интерфейса (ui_state.json).
/// </summary>
public sealed class GuiState
{
    public string TgPath { get; set; } = @"C:\Logs\TGLogs";
    public string TgFilter { get; set; } = "";
    public string TgTimeFrom { get; set; } = "";
    public string TgTimeTo { get; set; } = "";
    public int TgEventIndex { get; set; } = 0;
    public List<string> TgExcludeEvents { get; set; } = [];
    public bool TgIncRunning { get; set; }
    public bool TgIncCompleted { get; set; }
    public bool TgExRphost { get; set; }
    public bool TgExRmngr { get; set; }
    public bool TgExRagent { get; set; }
    public bool TgExCompleted { get; set; }
    public bool TgExRunning { get; set; }
    public int TgMinDurationIndex { get; set; } = 0;
    public int TgSortIndex { get; set; } = 0;
    public int TgLimitIndex { get; set; } = 5; // "Все записи"
    public List<string> TgIncludedUsers { get; set; } = [];
    public List<string> TgExcludedUsers { get; set; } = [];
    public List<string> TgIncludedApps { get; set; } = [];
    public List<string> TgExcludedApps { get; set; } = [];
    public List<string> TgIncludedPids { get; set; } = [];
    public List<string> TgExcludedPids { get; set; } = [];
    public List<string> TgIncludedSpids { get; set; } = [];
    public List<string> TgExcludedSpids { get; set; } = [];
    public List<string> TgIncludedThreads { get; set; } = [];
    public List<string> TgExcludedThreads { get; set; } = [];

    public string LgPath { get; set; } = @"C:\Logs\LGLogs";
    public string LgFilter { get; set; } = "";
    public string LgTimeFrom { get; set; } = "";
    public string LgTimeTo { get; set; } = "";
    public int LgImportanceIndex { get; set; } = 0;
    public bool LgIncError { get; set; }
    public bool LgIncWarn { get; set; }
    public bool LgIncInfo { get; set; }
    public bool LgIncNote { get; set; }
    public bool LgExError { get; set; }
    public bool LgExWarn { get; set; }
    public bool LgExInfo { get; set; }
    public bool LgExNote { get; set; }
    public List<string> LgExcludeEvents { get; set; } = [];
    public int LgSortIndex { get; set; } = 0;
    public int LgLimitIndex { get; set; } = 5; // "Все записи"
    public List<string> LgIncludedUsers { get; set; } = [];
    public List<string> LgExcludedUsers { get; set; } = [];
    public List<string> LgIncludedApps { get; set; } = [];
    public List<string> LgExcludedApps { get; set; } = [];
    public List<string> LgIncludedMetas { get; set; } = [];
    public List<string> LgExcludedMetas { get; set; } = [];
    public List<string> LgIncludedEvents { get; set; } = [];
    public List<string> LgExcludedEvents { get; set; } = [];

    public int SelectedTabIndex { get; set; } = 0;
}
