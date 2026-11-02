namespace Riftway.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_json_parser_options_t : uint
{
    JSON_PARSER_RFC = 0,
    JSON_PARSER_ALLOW_TRAILING_COMMAS = 1 << 0,
}
