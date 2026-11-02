using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;

namespace EmrresLauncherGui;

// Modrinth Arama Modeli
public class ModrinthResult
{
    public string ProjectId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
}

public partial class MainWindow : Window
{
    private readonly MinecraftPath _path;
    private readonly MinecraftLauncher _launcher;
    private readonly string _modsDirectory;
    private static readonly HttpClient _httpClient = new HttpClient();

    public MainWindow()
    {
        InitializeComponent();

        _path = new MinecraftPath();
        _launcher = new MinecraftLauncher(_path);

        _modsDirectory = Path.Combine(_path.BasePath, "mods");
        if (!Directory.Exists(_modsDirectory))
        {
            Directory.CreateDirectory(_modsDirectory);
        }

        // Modrinth API için zorunlu User-Agent başlığı
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "EmresLauncher/1.0 (emrelauncher@archlinux)");
        }

        SliderRam.ValueChanged += (s, e) =>
        {
            LblRam.Text = $"Verilecek RAM: {(int)SliderRam.Value} GB";
        };

        this.Loaded += async (s, e) =>
        {
            await LoadVersionsAsync();
            RefreshModList();
        };
    }

    #region --- SÜRÜM YÖNETİMİ & BAŞLATMA ---

    private async Task LoadVersionsAsync()
    {
        try
        {
            LblStatus.Text = "Sürümler Mojang'dan çekiliyor...";
            var versions = await _launcher.GetAllVersionsAsync();
            var releaseVersions = versions
                .Where(v => v.Type == "release" || v.Type == "custom")
                .Select(v => v.Name)
                .ToList();

            CmbVersions.ItemsSource = releaseVersions;

            if (releaseVersions.Contains("1.20.1"))
                CmbVersions.SelectedItem = "1.20.1";
            else if (releaseVersions.Count > 0)
                CmbVersions.SelectedIndex = 0;

            LblStatus.Text = "Oynamaya hazır.";
        }
        catch (Exception ex)
        {
            LblStatus.Text = "Sürüm listesi alınamadı: " + ex.Message;
            CmbVersions.ItemsSource = new[] { "1.20.1", "1.19.4", "1.16.5" };
            CmbVersions.SelectedIndex = 0;
        }
    }

    private async void BtnLaunch_Click(object? sender, RoutedEventArgs e)
    {
        string username = TxtUsername.Text?.Trim() ?? "EmrePlayer";
        if (string.IsNullOrWhiteSpace(username)) username = "EmrePlayer";

        string? selectedVersion = CmbVersions.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(selectedVersion))
        {
            LblStatus.Text = "[Hata] Lütfen bir sürüm seçin!";
            return;
        }

        int ramMb = (int)SliderRam.Value * 1024;
        BtnLaunch.IsEnabled = false;
        BtnLaunch.Content = "Başlatılıyor...";
        ProgBar.Value = 0;

        try
        {
            _launcher.FileProgressChanged += (s, ev) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    LblStatus.Text = $"İndiriliyor: {ev.Name}";
                    if (ev.TotalTasks > 0)
                        ProgBar.Value = (double)ev.ProgressedTasks / ev.TotalTasks * 100;
                });
            };

            var session = MSession.CreateOfflineSession(username);
            var launchOption = new MLaunchOption
            {
                MaximumRamMb = ramMb,
                Session = session
            };

            var process = await _launcher.CreateProcessAsync(selectedVersion, launchOption);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                process.StartInfo.EnvironmentVariables["__NV_PRIME_RENDER_OFFLOAD"] = "1";
                process.StartInfo.EnvironmentVariables["__GLX_VENDOR_LIBRARY_NAME"] = "nvidia";
                process.StartInfo.EnvironmentVariables["__VK_LAYER_NV_optimus"] = "NVIDIA_only";

                process.StartInfo.EnvironmentVariables["MESA_GL_VERSION_OVERRIDE"] = "4.5";
                process.StartInfo.EnvironmentVariables["MESA_GLSL_VERSION_OVERRIDE"] = "450";
            }

            process.Start();
            LblStatus.Text = $"{selectedVersion} RTX 4050 ile başlatıldı!";
            ProgBar.Value = 100;
        }
        catch (Exception ex)
        {
            LblStatus.Text = "[Hata] " + ex.Message;
        }
        finally
        {
            BtnLaunch.IsEnabled = true;
            BtnLaunch.Content = "Oyunu Başlat";
        }
    }

    #endregion

    #region --- MODRINTH ONLINE MOD İNDİRME ---

    private async void BtnSearchOnlineMods_Click(object? sender, RoutedEventArgs e)
    {
        string query = TxtModSearch.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(query)) return;

        LblOnlineStatus.Text = $"Modrinth üzerinde '{query}' aranıyor...";
        LstOnlineMods.ItemsSource = null;

        try
        {
            // Modrinth API Arama Sorgusu
            string url = $"https://api.modrinth.com/v2/search?query={Uri.EscapeDataString(query)}&limit=15&facets=[[\"project_type:mod\"]]";
            string response = await _httpClient.GetStringAsync(url);

            using var doc = JsonDocument.Parse(response);
            var hits = doc.RootElement.GetProperty("hits");

            var results = new List<ModrinthResult>();
            foreach (var hit in hits.EnumerateArray())
            {
                results.Add(new ModrinthResult
                {
                    ProjectId = hit.GetProperty("project_id").GetString() ?? "",
                    Title = hit.GetProperty("title").GetString() ?? "İsimsiz Mod",
                    Description = hit.GetProperty("description").GetString() ?? "Açıklama yok."
                });
            }

            LstOnlineMods.ItemsSource = results;
            LblOnlineStatus.Text = $"{results.Count} adet mod bulundu. Birini seçip 'İndir' butonuna basın.";
        }
        catch (Exception ex)
        {
            LblOnlineStatus.Text = "[Arama Hatası] " + ex.Message;
        }
    }

    private async void BtnDownloadOnlineMod_Click(object? sender, RoutedEventArgs e)
    {
        if (LstOnlineMods.SelectedItem is not ModrinthResult selectedMod)
        {
            LblOnlineStatus.Text = "Lütfen önce listeden indirmek istediğiniz bir modu seçin!";
            return;
        }

        string targetVersion = CmbVersions.SelectedItem?.ToString() ?? "1.20.1";
        BtnDownloadMod.IsEnabled = false;
        LblOnlineStatus.Text = $"{selectedMod.Title} için {targetVersion} uyumlu dosya aranıyor...";

        try
        {
            // Modun sürümlerini çek
            string versionsUrl = $"https://api.modrinth.com/v2/project/{selectedMod.ProjectId}/version";
            string versionsResponse = await _httpClient.GetStringAsync(versionsUrl);

            using var doc = JsonDocument.Parse(versionsResponse);
            var versionsArray = doc.RootElement.EnumerateArray();

            JsonElement? matchingVersion = null;

            // Önce seçili Minecraft sürümüne uygun olanı ara
            foreach (var ver in versionsArray)
            {
                var gameVersions = ver.GetProperty("game_versions").EnumerateArray().Select(g => g.GetString());
                if (gameVersions.Contains(targetVersion))
                {
                    matchingVersion = ver;
                    break;
                }
            }

            // Eğer tam sürüm bulunamazsa en son çıkan versiyonu al
            if (!matchingVersion.HasValue && doc.RootElement.GetArrayLength() > 0)
            {
                matchingVersion = doc.RootElement[0];
            }

            if (!matchingVersion.HasValue)
            {
                LblOnlineStatus.Text = $"Bu mod için uygun bir indirme dosyası bulunamadı!";
                return;
            }

            // Dosya indirme bağlantısını al
            var files = matchingVersion.Value.GetProperty("files");
            var primaryFile = files.EnumerateArray().FirstOrDefault();
            string downloadUrl = primaryFile.GetProperty("url").GetString()!;
            string fileName = primaryFile.GetProperty("filename").GetString()!;

            LblOnlineStatus.Text = $"{fileName} indiriliyor...";

            // .minecraft/mods klasörüne indir
            byte[] fileBytes = await _httpClient.GetByteArrayAsync(downloadUrl);
            string destination = Path.Combine(_modsDirectory, fileName);
            await File.WriteAllBytesAsync(destination, fileBytes);

            LblOnlineStatus.Text = $"✅ {fileName} başarıyla indirildi ve kuruldu!";
            RefreshModList(); // Yüklü modlar sekmesini anında güncelle
        }
        catch (Exception ex)
        {
            LblOnlineStatus.Text = "[İndirme Hatası] " + ex.Message;
        }
        finally
        {
            BtnDownloadMod.IsEnabled = true;
        }
    }

    #endregion

    #region --- YEREL MOD YÖNETİMİ ---

    private void RefreshModList()
    {
        if (!Directory.Exists(_modsDirectory)) return;

        var files = Directory.GetFiles(_modsDirectory)
            .Where(f => f.EndsWith(".jar") || f.EndsWith(".jar.disabled"))
            .Select(f =>
            {
                string fileName = Path.GetFileName(f);
                return fileName.EndsWith(".disabled") 
                    ? $"[PASİF] {fileName.Replace(".disabled", "")}" 
                    : $"[AKTİF] {fileName}";
            })
            .ToList();

        LstMods.ItemsSource = files;
    }

    private void BtnRefreshMods_Click(object? sender, RoutedEventArgs e) => RefreshModList();

    private void BtnOpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _modsDirectory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LblStatus.Text = "Klasör açılamadı: " + ex.Message;
        }
    }

    private async void BtnAddMod_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Mod Dosyası Seçin (.jar)",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("Mod Dosyası (*.jar)") { Patterns = new[] { "*.jar" } } }
        });

        foreach (var file in files)
        {
            string dest = Path.Combine(_modsDirectory, file.Name);
            using var src = await file.OpenReadAsync();
            using var dst = File.Create(dest);
            await src.CopyToAsync(dst);
        }
        RefreshModList();
    }

    private void BtnToggleMod_Click(object? sender, RoutedEventArgs e)
    {
        if (LstMods.SelectedItem is not string selected) return;
        string cleanName = selected.Replace("[AKTİF] ", "").Replace("[PASİF] ", "");
        string active = Path.Combine(_modsDirectory, cleanName);
        string disabled = Path.Combine(_modsDirectory, cleanName + ".disabled");

        if (File.Exists(active)) File.Move(active, disabled);
        else if (File.Exists(disabled)) File.Move(disabled, active);
        RefreshModList();
    }

    private void BtnDeleteMod_Click(object? sender, RoutedEventArgs e)
    {
        if (LstMods.SelectedItem is not string selected) return;
        string cleanName = selected.Replace("[AKTİF] ", "").Replace("[PASİF] ", "");
        string active = Path.Combine(_modsDirectory, cleanName);
        string disabled = Path.Combine(_modsDirectory, cleanName + ".disabled");

        if (File.Exists(active)) File.Delete(active);
        if (File.Exists(disabled)) File.Delete(disabled);
        RefreshModList();
    }

    #endregion
}