using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vedar.Infrastructure.Models;

namespace Vedar.Tests.Build;

internal static class Repo
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "Vedar.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    public static string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    public static string Read(params string[] parts) => File.ReadAllText(Path(parts));
}

/// <summary>Checks the build and installer definitions (always) and the produced artifacts (when they exist).</summary>
public class PackagingTests
{
    [Fact]
    public void Installer_script_defines_a_proper_windows_installer()
    {
        var iss = Repo.Read("installer", "VEDAR.iss");
        Assert.Contains("AppId={{8F0E9A2C-5B1D-4E7A-9C3F-2D6B7A1E4F90}", iss);
        Assert.Contains("OutputBaseFilename=VEDAR-Setup-v{#AppVersion}-x64", iss);
        Assert.Contains("#define AppVersion \"0.1.0\"", iss);
        Assert.Contains("#define AppExe \"VEDAR.exe\"", iss);
        Assert.Contains("PrivilegesRequired=lowest", iss); // no administrator rights needed
        Assert.Contains("ArchitecturesInstallIn64BitMode=x64compatible", iss);
        Assert.Contains("SetupIconFile=..\\assets\\branding\\vedar.ico", iss);
        Assert.Contains("compiler:Languages\\Bulgarian.isl", iss);
        Assert.Contains("[Icons]", iss);
        Assert.Contains("HotKey: ctrl+alt+v", iss);
        Assert.Contains("Software\\Microsoft\\Windows\\CurrentVersion\\Run", iss);
        Assert.Contains("--background", iss);
        Assert.Contains("Tasks: autostart", iss);
        Assert.Contains("RegDeleteValue(HKCU, RunKey, 'VEDAR')", iss); // no dangling autostart after uninstall
        Assert.Contains("MB_DEFBUTTON2", iss); // deleting personal data defaults to "No"
        Assert.Contains("VerifyModels", iss);
        Assert.Contains("GetSHA256OfFile", iss);
        Assert.Contains("'--exit'", iss); // closes a running VEDAR before upgrading
        Assert.True(File.ReadAllBytes(Repo.Path("installer", "VEDAR.iss")).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "UTF-8 BOM needed for Cyrillic text");
        foreach (var asset in new[] { "vedar.ico", "wizard-image.bmp", "wizard-image-2x.bmp", "wizard-small.bmp", "wizard-small-2x.bmp" })
        {
            Assert.True(File.Exists(Repo.Path("assets", "branding", asset)), asset);
        }
    }

    [Fact]
    public void Build_scripts_exist_and_produce_the_required_installer_name()
    {
        foreach (var script in new[] { "build.ps1", "publish.ps1", "create-installer.ps1", "fetch-models.ps1", "common.ps1" })
        {
            Assert.True(File.Exists(Repo.Path("build", script)), script);
        }

        Assert.Contains("--self-contained', 'true'", Repo.Read("build", "publish.ps1"));
        Assert.Contains("'-r', 'win-x64'", Repo.Read("build", "publish.ps1"));
        Assert.Contains("VEDAR-Setup-v$script:VedarVersion-x64.exe", Repo.Read("build", "create-installer.ps1"));
        Assert.Contains("Get-AuthenticodeSignature", Repo.Read("build", "create-installer.ps1"));
        Assert.Contains("Invoke-VerifiedDownload", Repo.Read("build", "fetch-models.ps1"));
    }

    [Fact]
    public void Version_is_consistent_everywhere()
    {
        Assert.Contains("<VedarVersion>0.1.0</VedarVersion>", Repo.Read("Directory.Build.props"));
        Assert.Contains("$script:VedarVersion = '0.1.0'", Repo.Read("build", "common.ps1"));
        Assert.Contains("#define AppVersion \"0.1.0\"", Repo.Read("installer", "VEDAR.iss"));
        Assert.Contains("assemblyIdentity version=\"0.1.0.0\"", Repo.Read("src", "Vedar.Desktop", "app.manifest"));
    }

    [Fact]
    public void All_downloads_are_pinned_and_checksummed()
    {
        using var doc = JsonDocument.Parse(Repo.Read("build", "dependencies.json"));
        var models = doc.RootElement.GetProperty("models").EnumerateArray().ToList();
        Assert.NotEmpty(models);
        foreach (var m in models)
        {
            var url = m.GetProperty("url").GetString()!;
            Assert.StartsWith("https://huggingface.co/", url);
            Assert.Matches("/resolve/[0-9a-f]{40}/", url); // immutable revision, not "main"
            Assert.Matches("^[0-9a-f]{64}$", m.GetProperty("sha256").GetString()!);
            Assert.True(m.GetProperty("size").GetInt64() > 0);
        }

        // The Whisper models in the build manifest are the ones the app knows (same hashes) and multilingual.
        foreach (var m in models.Where(m => m.GetProperty("path").GetString()!.StartsWith("whisper/")))
        {
            var catalog = ModelCatalog.FindWhisperByFile(System.IO.Path.GetFileName(m.GetProperty("path").GetString()!));
            Assert.NotNull(catalog);
            Assert.Equal(catalog!.Sha256, m.GetProperty("sha256").GetString());
            Assert.DoesNotContain(".en.", m.GetProperty("path").GetString());
        }

        Assert.Contains(models, m => m.GetProperty("path").GetString() == "piper/bg_BG-dimitar-medium/bg_BG-dimitar-medium.onnx");
        Assert.Matches("^[0-9a-f]{64}$", doc.RootElement.GetProperty("vcRedist").GetProperty("sha256").GetString()!);
        Assert.Matches("^[0-9a-f]{64}$", doc.RootElement.GetProperty("innoSetup").GetProperty("sha256").GetString()!);
    }

    [Fact]
    public void Repository_contains_no_api_keys_or_secret_files()
    {
        var keyPattern = new Regex(@"(?<![A-Za-z0-9])(sk-[A-Za-z0-9]{24,}|Bearer\s+[A-Za-z0-9._\-]{30,})");
        var skipDirs = new[] { ".git", "bin", "obj", "models", "artifacts", "dist", ".tools", "TestResults", "node_modules" };
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Repo.Root, "*", SearchOption.AllDirectories))
        {
            var rel = System.IO.Path.GetRelativePath(Repo.Root, file);
            if (rel.Split(System.IO.Path.DirectorySeparatorChar).Any(p => skipDirs.Contains(p)))
            {
                continue;
            }

            var name = System.IO.Path.GetFileName(file);
            if (name.EndsWith(".key", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".key.dat", StringComparison.OrdinalIgnoreCase) || name == ".env")
            {
                offenders.Add(rel);
                continue;
            }

            if (new FileInfo(file).Length > 2_000_000 || IsBinary(file))
            {
                continue;
            }

            if (keyPattern.IsMatch(File.ReadAllText(file)) && !rel.Contains("Tests"))
            {
                offenders.Add(rel);
            }
        }

        Assert.True(offenders.Count == 0, "Possible secrets: " + string.Join(", ", offenders));
        Assert.Contains("secrets/", Repo.Read(".gitignore"));
        Assert.Contains("models/**", Repo.Read(".gitignore"));
    }

    private static bool IsBinary(string file)
    {
        var buffer = new byte[512];
        using var fs = File.OpenRead(file);
        var n = fs.Read(buffer, 0, buffer.Length);
        return buffer.Take(n).Any(b => b == 0);
    }

    [Fact]
    public void Published_application_is_self_contained_and_complete()
    {
        var dir = Repo.Path("artifacts", "publish", "win-x64");
        Assert.SkipUnless(File.Exists(System.IO.Path.Combine(dir, "VEDAR.exe")), "Run build/publish.ps1 first (this test checks the real publish output).");
        foreach (var f in new[] { "VEDAR.exe", "VEDAR.dll", "coreclr.dll", "hostfxr.dll", "PresentationFramework.dll", "System.Windows.Forms.dll",
                     "onnxruntime.dll", "sherpa-onnx-c-api.dll", "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll", "vcomp140.dll",
                     "runtimes/win-x64/whisper.dll", "runtimes/win-x64/ggml-cpu-whisper.dll", "docs/THIRD_PARTY_LICENSES.md", "docs/LICENSE", "build-info.txt" })
        {
            Assert.True(File.Exists(System.IO.Path.Combine(dir, f)), f);
        }

        Assert.False(Directory.Exists(System.IO.Path.Combine(dir, "secrets")));
        Assert.False(File.Exists(System.IO.Path.Combine(dir, "settings.json")));
        Assert.Empty(Directory.GetFiles(dir, "*.pdb"));

        // Every bundled model matches its recorded checksum.
        var sums = File.ReadAllLines(System.IO.Path.Combine(dir, "models", "SHA256SUMS.txt"));
        Assert.True(sums.Length >= 10);
        foreach (var line in sums)
        {
            var hash = line[..64];
            var rel = line[66..];
            using var stream = File.OpenRead(System.IO.Path.Combine(dir, "models", rel));
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
        }
    }

    [Fact]
    public void Installer_exe_is_a_real_inno_setup_executable_with_checksum()
    {
        var setup = Repo.Path("dist", "VEDAR-Setup-v0.1.0-x64.exe");
        Assert.SkipUnless(File.Exists(setup), "Run build/build.ps1 first (this test checks the real installer).");
        var info = new FileInfo(setup);
        Assert.True(info.Length > 100 * 1024 * 1024, "the installer bundles the speech models");
        using var fs = File.OpenRead(setup);
        var head = new byte[2];
        fs.ReadExactly(head);
        Assert.Equal("MZ", Encoding.ASCII.GetString(head));
        fs.Position = 0;
        var firstMb = new byte[4 * 1024 * 1024];
        var read = fs.Read(firstMb, 0, firstMb.Length);
        Assert.Contains("Inno Setup", Encoding.ASCII.GetString(firstMb, 0, read));

        var sumFile = setup + ".sha256";
        Assert.True(File.Exists(sumFile));
        fs.Position = 0;
        Assert.StartsWith(Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant(), File.ReadAllText(sumFile));
    }
}
