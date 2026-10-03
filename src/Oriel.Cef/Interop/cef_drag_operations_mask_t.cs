namespace Oriel.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_drag_operations_mask_t : uint
{
    DRAG_OPERATION_NONE = 0,
    DRAG_OPERATION_COPY = 1,
    DRAG_OPERATION_LINK = 2,
    DRAG_OPERATION_GENERIC = 4,
    DRAG_OPERATION_PRIVATE = 8,
    DRAG_OPERATION_MOVE = 16,
    DRAG_OPERATION_DELETE = 32,
    DRAG_OPERATION_EVERY = (2147483647 * 2U + 1U),
}
