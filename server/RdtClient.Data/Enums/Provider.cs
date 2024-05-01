using System.ComponentModel;

namespace RdtClient.Data.Enums;

public enum Provider
{
    [Description("Torbox")]
    Torbox,
    
    [Description("RealDebrid")]
    RealDebrid,

    [Description("AllDebrid")]
    AllDebrid,

    [Description("Premiumize")]
    Premiumize
}
