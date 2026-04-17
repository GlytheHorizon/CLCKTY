using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CLCKTY.App.Core;
using NAudio.Wave;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using Forms = System.Windows.Forms;

namespace CLCKTY.App.UI;

public partial class RecordItWindow : Window
{
    private static readonly string CustomPacksFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CLCKTY", "CustomPacks");

    private const double MaxRecordingSeconds = 5.0;

    private readonly ISoundEngine _soundEngine;
    private string? _currentPackFolder;
    private readonly List<string> _currentPackFiles = new();

    private WaveInEvent? _waveIn;
    private WaveFileWriter? _waveWriter;
    private string? _recordingFilePath;
    private bool _isRecording;
    private DateTime _recordingStartUtc;
    private DispatcherTimer? _recordingTimer;

    public RecordItWindow(ISoundEngine soundEngine)
    {
        InitializeComponent();
        _soundEngine = soundEngine;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        StopRecordingIfActive();
        Hide();
    }

    private void CreatePackButton_Click(object sender, RoutedEventArgs e)
    {
        var packName = PackNameTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(packName))
        {
            PackStatusText.Text = "Please enter a pack name.";
            PackStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#FF7043"));
            return;
        }

        // Sanitize folder name
        var safeName = string.Join("_", packName.Split(Path.GetInvalidFileNameChars()));
        _currentPackFolder = Path.Combine(CustomPacksFolder, safeName);

        try
        {
            Directory.CreateDirectory(_currentPackFolder);
            _currentPackFiles.Clear();

            // Scan for existing files
            foreach (var file in Directory.EnumerateFiles(_currentPackFolder, "*.wav"))
            {
                _currentPackFiles.Add(file);
            }

            RefreshPackSoundsList();
            PackStatusText.Text = $"Pack '{packName}' ready at:\n{_currentPackFolder}";
            PackStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#22D883"));
        }
        catch (Exception ex)
        {
            PackStatusText.Text = $"Error creating pack: {ex.Message}";
            PackStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#FF7043"));
        }
    }

    private void BulkUploadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPackFolder == null)
        {
            PackStatusText.Text = "Create a pack first before uploading sounds.";
            PackStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#FF7043"));
            return;
        }

        using var dialog = new Forms.OpenFileDialog
        {
            Title = "Select audio files to add to pack",
            Filter = "Audio files|*.wav;*.mp3;*.ogg;*.flac;*.aac;*.m4a|All files|*.*",
            Multiselect = true,
            CheckFileExists = true,
            RestoreDirectory = true
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        var addedCount = 0;
        foreach (var file in dialog.FileNames)
        {
            try
            {
                var destPath = Path.Combine(_currentPackFolder, Path.GetFileName(file));
                if (File.Exists(destPath))
                {
                    destPath = Path.Combine(_currentPackFolder,
                        $"{Path.GetFileNameWithoutExtension(file)}_{DateTime.Now:HHmmss}{Path.GetExtension(file)}");
                }

                File.Copy(file, destPath);
                _currentPackFiles.Add(destPath);
                addedCount++;
            }
            catch
            {
                // skip files that fail to copy
            }
        }

        RefreshPackSoundsList();
        PackStatusText.Text = $"Added {addedCount} sound(s) to pack.";
        PackStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#22D883"));

        // Auto-import the pack
        AutoImportCurrentPack();
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRecording)
        {
            StopRecordingIfActive();
            return;
        }

        if (_currentPackFolder == null)
        {
            PackStatusText.Text = "Create a pack first before recording.";
            PackStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#FF7043"));
            return;
        }

        StartRecording();
    }

    private void StartRecording()
    {
        try
        {
            var fileName = $"recording_{DateTime.Now:yyyyMMdd_HHmmss}.wav";
            _recordingFilePath = Path.Combine(_currentPackFolder!, fileName);

            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(44100, 16, 1),
                BufferMilliseconds = 100
            };

            _waveWriter = new WaveFileWriter(_recordingFilePath, _waveIn.WaveFormat);

            _waveIn.DataAvailable += (s, e) =>
            {
                if (_waveWriter != null && _isRecording)
                {
                    _waveWriter.Write(e.Buffer, 0, e.BytesRecorded);
                }
            };

            _waveIn.RecordingStopped += (s, e) =>
            {
                _waveWriter?.Dispose();
                _waveWriter = null;
                _waveIn?.Dispose();
                _waveIn = null;
            };

            _isRecording = true;
            _recordingStartUtc = DateTime.UtcNow;
            _waveIn.StartRecording();

            RecordingStatusText.Text = "Recording...";
            RecordingStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#FF5252"));
            RecordButtonIcon.Text = "\uE71A"; // Stop icon

            // Start timer
            _recordingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _recordingTimer.Tick += RecordingTimer_Tick;
            _recordingTimer.Start();
        }
        catch (Exception ex)
        {
            RecordingStatusText.Text = $"Mic error: {ex.Message}";
            RecordingStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#FF7043"));
            _isRecording = false;
        }
    }

    private void RecordingTimer_Tick(object? sender, EventArgs e)
    {
        var elapsed = (DateTime.UtcNow - _recordingStartUtc).TotalSeconds;
        RecordingTimerText.Text = $"{elapsed:F1} / {MaxRecordingSeconds:F1} sec";

        // Update progress bar
        var progressParent = RecordingProgressFill.Parent as Grid;
        if (progressParent != null)
        {
            var parentWidth = progressParent.ActualWidth;
            if (parentWidth > 0)
            {
                RecordingProgressFill.Width = parentWidth * Math.Min(1.0, elapsed / MaxRecordingSeconds);
            }
        }

        if (elapsed >= MaxRecordingSeconds)
        {
            StopRecordingIfActive();
        }
    }

    private void StopRecordingIfActive()
    {
        if (!_isRecording) return;
        _isRecording = false;

        _recordingTimer?.Stop();
        _recordingTimer = null;

        try
        {
            _waveIn?.StopRecording();
        }
        catch { /* ignore */ }

        RecordingStatusText.Text = "Recording saved!";
        RecordingStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#22D883"));
        RecordButtonIcon.Text = "\uE720"; // Mic icon
        RecordingProgressFill.Width = 0;

        if (_recordingFilePath != null && File.Exists(_recordingFilePath))
        {
            _currentPackFiles.Add(_recordingFilePath);
            RefreshPackSoundsList();
            AutoImportCurrentPack();
        }

        RecordingTimerText.Text = $"0.0 / {MaxRecordingSeconds:F1} sec";
    }

    private void RefreshPackSoundsList()
    {
        PackSoundsPanel.Children.Clear();

        if (_currentPackFiles.Count == 0)
        {
            PackSoundsPanel.Children.Add(new TextBlock
            {
                Text = "No sounds added yet.",
                Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#5B8A76")),
                FontStyle = FontStyles.Italic,
                FontSize = 11
            });
            PackSoundCountText.Text = "0 sounds";
            return;
        }

        PackSoundCountText.Text = $"{_currentPackFiles.Count} sound(s)";

        foreach (var filePath in _currentPackFiles)
        {
            var name = Path.GetFileName(filePath);

            var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textBlock = new TextBlock
            {
                Text = $"  {name}",
                Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#CFECE0")),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var deleteBtn = new WpfButton
            {
                Content = "✕",
                Style = (Style)FindResource("DangerButtonStyle"),
                Tag = filePath,
                FontSize = 10,
                Width = 24,
                Height = 24,
                Padding = new Thickness(0),
                Margin = new Thickness(4, 0, 0, 0)
            };
            deleteBtn.Click += DeleteSoundButton_Click;

            Grid.SetColumn(textBlock, 0);
            Grid.SetColumn(deleteBtn, 1);
            grid.Children.Add(textBlock);
            grid.Children.Add(deleteBtn);

            PackSoundsPanel.Children.Add(grid);
        }
    }

    private void DeleteSoundButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton btn && btn.Tag is string filePath)
        {
            try
            {
                if (File.Exists(filePath)) File.Delete(filePath);
                _currentPackFiles.Remove(filePath);
                RefreshPackSoundsList();
            }
            catch { /* ignore */ }
        }
    }

    private async void AutoImportCurrentPack()
    {
        if (_currentPackFolder == null) return;

        try
        {
            await _soundEngine.ImportSoundPackAsync(_currentPackFolder, false);
            PackStatusText.Text = "Pack auto-imported and available in Sound Profiles!";
            PackStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#22D883"));
        }
        catch
        {
            // silently fail auto-import
        }
    }
}
