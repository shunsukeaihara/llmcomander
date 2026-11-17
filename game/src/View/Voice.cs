using Godot;

namespace LlmCommander.View;

/// <summary>
/// マイク入力の録音。押している間だけ録音し、離したら WAV のバイト列を返す。
/// 録音用のバスは音量を絞って、スピーカーから自分の声が出ないようにする。
/// </summary>
public partial class Voice : Node
{
    private AudioEffectRecord? _record;
    private AudioStreamPlayer? _player;
    public bool Available { get; private set; }
    public bool Recording { get; private set; }
    private double _startedAt;

    public override void _Ready()
    {
        if (AudioServer.GetInputDeviceList().Length == 0) return;
        int bus = AudioServer.BusCount;
        AudioServer.AddBus(bus);
        AudioServer.SetBusName(bus, "Record");
        AudioServer.SetBusVolumeDb(bus, -80);
        _record = new AudioEffectRecord { Format = AudioStreamWav.FormatEnum.Format16Bits };
        AudioServer.AddBusEffect(bus, _record);
        _player = new AudioStreamPlayer { Stream = new AudioStreamMicrophone(), Bus = "Record" };
        AddChild(_player);
        _player.Play();
        Available = true;
    }

    public void Start()
    {
        if (_record == null || Recording) return;
        _record.SetRecordingActive(true);
        Recording = true;
        _startedAt = Time.GetTicksMsec() / 1000.0;
    }

    /// <summary>録音を止めて WAV を返す。短すぎる録音(押し間違い)は null。</summary>
    public byte[]? Stop()
    {
        if (_record == null || !Recording) return null;
        Recording = false;
        _record.SetRecordingActive(false);
        var wav = _record.GetRecording();
        if (wav == null || Time.GetTicksMsec() / 1000.0 - _startedAt < 0.35) return null;
        string path = OS.GetUserDataDir() + "/last_utterance.wav";
        wav.SaveToWav(path);
        return System.IO.File.ReadAllBytes(path);
    }
}
