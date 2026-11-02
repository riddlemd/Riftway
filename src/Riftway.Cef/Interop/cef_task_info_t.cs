namespace Riftway.Cef.Interop;

public partial struct cef_task_info_t
{
    [NativeTypeName("size_t")]
    public nuint size;

    [NativeTypeName("int64_t")]
    public long id;

    public cef_task_type_t type;

    public int is_killable;

    [NativeTypeName("cef_string_t")]
    public cef_string_utf16_t title;

    public double cpu_usage;

    public int number_of_processors;

    [NativeTypeName("int64_t")]
    public long memory;

    [NativeTypeName("int64_t")]
    public long gpu_memory;

    public int is_gpu_memory_inflated;
}
