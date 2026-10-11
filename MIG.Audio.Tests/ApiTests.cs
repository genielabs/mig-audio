using System.Text;
using MIG;
using MIG.Interfaces.Audio;
using NUnit.Framework;

namespace MIG.Audio.Tests;

public class ApiTests
{
    [Test]
    public void ConfigurationReplacementKeepsDefaultsAndExplicitValues()
    {
        var synthOptions = new List<MIG.Config.Option> { new("speed", "1.2"), new("Voice", " ") };
        var synth = new VoiceSynthesizer { Options = synthOptions };
        var recognizerOptions = new List<MIG.Config.Option>();
        var recognizer = new VoiceRecognizer { Options = recognizerOptions };
        Assert.Multiple(() =>
        {
            Assert.That(synth.Options, Is.SameAs(synthOptions));
            Assert.That(synth.Options.Find(o => o.Name == "speed")!.Value, Is.EqualTo("1.2"));
            Assert.That(synth.Options.Find(o => o.Name == "Voice")!.Value, Is.EqualTo("it_IT-paola-medium"));
            Assert.That(synth.Options.Find(o => o.Name == "Threads")!.Value, Is.EqualTo("2"));
            Assert.That(recognizer.Options, Is.SameAs(recognizerOptions));
            Assert.That(recognizer.Options.Count, Is.EqualTo(4));
            Assert.That(recognizer.Options.Find(o => o.Name == "Language")!.Value, Is.EqualTo("auto"));
            Assert.That(recognizer.IsConnected, Is.False);
            Assert.That(synth.IsConnected, Is.False);
        });
    }

    [TestCase("1.15", 1.15f)][TestCase("1", 1f)]
    public void SpeedAcceptsInvariantNumbers(string input, float expected) => Assert.That(AudioRequest.Speed(input), Is.EqualTo(expected));
    [TestCase("NaN")][TestCase("Infinity")][TestCase("0")][TestCase("-1")][TestCase("1,2")]
    public void InvalidSpeedRejected(string input) => Assert.Throws<ArgumentException>(() => AudioRequest.Speed(input));
    [TestCase("../escape")][TestCase("a/b")][TestCase("a\\b")][TestCase("..")]
    public void ModelPathsRejected(string input) => Assert.Throws<ArgumentException>(() => AudioRequest.Model(input));

    [Test]
    public void OptionsDefaultAndUtf8Payload()
    {
        var request = new MigInterfaceCommand("Audio.VoiceSynthesizer/chat-2/Voice.Synthesize/default/it_IT-paola-medium", Encoding.UTF8.GetBytes("Città"));
        Assert.That(AudioRequest.Option(request, 0, "1.0"), Is.EqualTo("1.0"));
        Assert.That(AudioRequest.Option(request, 1, "other"), Is.EqualTo("it_IT-paola-medium"));
        Assert.That(AudioRequest.BodyText(request.Data), Is.EqualTo("Città"));
    }
    [Test]
    public void PcmDurationAndTruncatedSamples()
    {
        Assert.That(AudioRequest.Duration(new byte[32000]), Is.EqualTo(1));
        Assert.Throws<ArgumentException>(() => AudioRequest.Duration(new byte[3]));
    }
    [Test]
    public void WavDurationExcludesHeader()
    {
        using var ms = new MemoryStream(); using var writer = new BinaryWriter(ms);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(32036);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
        writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(32000); writer.Write(new byte[32000]);
        Assert.That(AudioRequest.Duration(ms.ToArray()), Is.EqualTo(1));
        Assert.Throws<ArgumentException>(() => AudioRequest.Duration(ms.ToArray()[..30]));
    }
    [Test]
    public void SetLanguageUsesBodyWithoutLoadingModel()
    {
        var recognizer = new VoiceRecognizer();
        recognizer.InterfaceControl(new MigInterfaceCommand("Audio.VoiceRecognizer/0/Voice.SetLanguage", Encoding.UTF8.GetBytes("it")));
        Assert.That(recognizer.Options.Find(o => o.Name == "Language")!.Value, Is.EqualTo("it"));
        Assert.That(recognizer.IsConnected, Is.False);
    }
    [Test]
    public void DisabledDoesNotCreateModulesOrLoadEngine()
    {
        var synth = new VoiceSynthesizer { IsEnabled = false };
        synth.InterfaceControl(new MigInterfaceCommand("Audio.VoiceSynthesizer/chat/Voice.Synthesize", "hello"));
        Assert.That(synth.GetModules().Count, Is.EqualTo(1));
        Assert.That(synth.IsConnected, Is.False);
    }
    [Test]
    public void ModuleRegisteredBeforeEventsWithoutChangingDefaults()
    {
        var synth = new VoiceSynthesizer(); var order = new List<string>();
        synth.InterfaceModulesChanged += (_, _) => order.Add("module");
        synth.InterfacePropertyChanged += (_, _) => order.Add("property");
        synth.InterfaceControl(new MigInterfaceCommand("Audio.VoiceSynthesizer/chat-1/Voice.Synthesize/1.2/nonexistent-test-voice", "hello"));
        Assert.That(order[0], Is.EqualTo("module"));
        Assert.That(synth.GetModules().Any(m => m.Address == "chat-1"), Is.True);
        Assert.That(synth.Options.Find(o => o.Name == "Speed")!.Value, Is.EqualTo("1.0"));
        Assert.That(synth.Options.Find(o => o.Name == "Voice")!.Value, Is.EqualTo("it_IT-paola-medium"));
    }
}
