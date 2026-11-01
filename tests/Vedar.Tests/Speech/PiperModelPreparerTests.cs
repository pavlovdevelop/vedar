using Vedar.Speech.Synthesis;
using Vedar.Tests.Support;

namespace Vedar.Tests.Speech;

public class PiperModelPreparerTests
{
    [Fact]
    public void Appended_metadata_can_be_read_back_from_protobuf()
    {
        var dir = TestModels.NewTempDir();
        var path = Path.Combine(dir, "m.onnx");
        // Minimal ModelProto: ir_version=8 (field 1, varint) and producer_name="t" (field 2, string).
        File.WriteAllBytes(path, [0x08, 0x08, 0x12, 0x01, (byte)'t']);
        using (var fs = new FileStream(path, FileMode.Append))
        {
            PiperModelPreparer.AppendMetadata(fs, new Dictionary<string, string> { ["model_type"] = "vits", ["comment"] = "piper", ["език"] = "български" });
        }

        var meta = PiperModelPreparer.ReadMetadata(path);
        Assert.Equal("vits", meta["model_type"]);
        Assert.Equal("piper", meta["comment"]);
        Assert.Equal("български", meta["език"]);
    }

    [Fact]
    public void Prepares_official_voice_without_changing_the_original_file()
    {
        var onnx = TestModels.PiperVoice;
        var espeak = TestModels.EspeakData;
        Assert.SkipWhen(onnx is null || espeak is null, TestModels.MissingMessage("The Piper voice"));
        var before = File.GetLastWriteTimeUtc(onnx!);
        var cache = TestModels.NewTempDir();
        var prepared = PiperModelPreparer.Prepare(onnx!, onnx + ".json", espeak!, cache);

        Assert.Equal(before, File.GetLastWriteTimeUtc(onnx!));
        Assert.Equal(22050, prepared.Config.SampleRate);
        Assert.Equal("bg", prepared.Config.EspeakVoice);
        var meta = PiperModelPreparer.ReadMetadata(prepared.ModelPath);
        Assert.Equal("vits", meta["model_type"]);
        Assert.Equal("piper", meta["comment"]);
        Assert.Equal("bg", meta["voice"]);
        var tokens = File.ReadAllLines(prepared.TokensPath);
        Assert.Contains("_ 0", tokens);
        Assert.Contains("^ 1", tokens);
        Assert.True(new FileInfo(prepared.ModelPath).Length > new FileInfo(onnx!).Length);
    }
}
