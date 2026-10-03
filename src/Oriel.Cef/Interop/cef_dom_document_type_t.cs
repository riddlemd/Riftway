namespace Oriel.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_dom_document_type_t : uint
{
    DOM_DOCUMENT_TYPE_UNKNOWN,
    DOM_DOCUMENT_TYPE_HTML,
    DOM_DOCUMENT_TYPE_XHTML,
    DOM_DOCUMENT_TYPE_PLUGIN,
    DOM_DOCUMENT_TYPE_NUM_VALUES,
}
