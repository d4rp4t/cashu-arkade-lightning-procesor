using System.Runtime.CompilerServices;

namespace cdk_arkade_payment_processor.Services;

public static class VersionValidatoor
{
    public static string ProtocolVersion = "3.0";
    
    public static bool ValidateVersion(this HttpContext ctx)
    {
        return ctx.Request.Headers["x-cdk-protocol-version"] == ProtocolVersion
               //todo remove it after cdk 0.17rc1
               || ctx.Request.Headers["x-cdk-protocol-version"] == "3.0.0";
    }
}