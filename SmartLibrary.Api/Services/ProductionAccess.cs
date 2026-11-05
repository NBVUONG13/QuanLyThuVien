using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace SmartLibrary.Api.Services;

/// <summary>
/// Deny by default on cloud; the operator enables public API only after
/// applying the session SQL migration and configuring restricted secrets.
/// </summary>
public static class ProductionAccess
{
    public static bool IsEnabled(WebApplication app) =>
        app.Environment.IsDevelopment() ||
        app.Configuration.GetValue<bool>("SmartLibrary:EnableProductionApi");
}
