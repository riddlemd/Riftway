namespace Oriel.Cef.Interop;

public partial struct cef_audio_parameters_t
{
    [NativeTypeName("size_t")]
    public nuint size;

    public cef_channel_layout_t channel_layout;

    public int sample_rate;

    public int frames_per_buffer;
}
