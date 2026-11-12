using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AccessProbe.LinuxBackend;
public static class Protocol
{
    public const int RequestLimit=2*1024*1024, ResponseLimit=16*1024*1024;
    public static readonly JsonSerializerOptions Json=new()
    {
        PropertyNamingPolicy=JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,
        Converters={new JsonStringEnumConverter(JsonNamingPolicy.CamelCase,allowIntegerValues:false)}
    };
}
sealed class ProtocolError(string code,string message):Exception(message) { public string Code {get;}=code; }
sealed class BoundedLineReader(Stream input)
{
    private readonly byte[] buffer=new byte[8192];
    private int offset,count;
    public async Task<(string? Line,bool TooLarge)> ReadAsync(CancellationToken cancellation)
    {
        using var line=new MemoryStream(); bool overflow=false;
        while(true)
        {
            if(offset==count){count=await input.ReadAsync(buffer,cancellation);offset=0;if(count==0)break;}
            var start=offset;
            while(offset<count && buffer[offset]!=10)offset++;
            var length=offset-start;
            if(line.Length+length>Protocol.RequestLimit)overflow=true;
            if(!overflow)line.Write(buffer,start,length);
            if(offset<count){offset++;return(overflow?"":Decode(line),overflow);}
        }
        return(line.Length==0&&!overflow?null:overflow?"":Decode(line),overflow);
    }
    private static string Decode(MemoryStream line)
    {
        try{return new UTF8Encoding(false,true).GetString(line.GetBuffer(),0,(int)line.Length).TrimEnd('\r');}
        catch(DecoderFallbackException){return "{";}
    }
}
