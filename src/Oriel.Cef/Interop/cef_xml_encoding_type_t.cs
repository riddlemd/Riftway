namespace Oriel.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_xml_encoding_type_t : uint
{
    XML_ENCODING_NONE,
    XML_ENCODING_UTF8,
    XML_ENCODING_UTF16LE,
    XML_ENCODING_UTF16BE,
    XML_ENCODING_ASCII,
    XML_ENCODING_NUM_VALUES,
}
