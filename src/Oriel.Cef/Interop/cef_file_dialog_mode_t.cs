namespace Oriel.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_file_dialog_mode_t : uint
{
    FILE_DIALOG_OPEN,
    FILE_DIALOG_OPEN_MULTIPLE,
    FILE_DIALOG_OPEN_FOLDER,
    FILE_DIALOG_SAVE,
    FILE_DIALOG_NUM_VALUES,
}
