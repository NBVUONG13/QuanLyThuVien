using Microsoft.AspNetCore.RateLimiting;

namespace SmartLibrary.Api.Services;

public static class EmailRegistrationEndpoints
{
    public static IEndpointRouteBuilder MapEmailRegistrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth/email-register");

        group.MapPost("/start", async (EmailRegisterStart body,
            EmailRegistrationService registration, CancellationToken ct) =>
        {
            try
            {
                var result = await registration.StartAsync(body, ct);
                return Results.Ok(new
                {
                    message = "email_otp_sent",
                    requestId = result.RequestId,
                    expiresAt = result.ExpiresAt,
                    notice = "Ma xac minh da duoc gui den email. Vui long kiem tra Hop thu den/Spam."
                });
            }
            catch (EmailRegistrationException ex)
            {
                int status = ex.Code switch
                {
                    "account_exists" => 409,
                    "try_later" => 429,
                    _ => 400
                };
                return Results.Json(new { code = ex.Code, message = ex.Message }, statusCode: status);
            }
            catch (EmailOtpDeliveryException ex)
            {
                return Results.Json(new { code = "email_delivery_failed", message = ex.Message }, statusCode: 503);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Results.StatusCode(499);
            }
            catch (Exception)
            {
                return Results.Json(new
                {
                    code = "server_error",
                    message = "Khong tao duoc yeu cau dang ky. Kiem tra cau hinh email va database."
                }, statusCode: 503);
            }
        }).RequireRateLimiting("register");

        group.MapPost("/confirm", async (EmailRegisterConfirm body,
            EmailRegistrationService registration, CancellationToken ct) =>
        {
            try
            {
                var result = await registration.ConfirmAsync(body, ct);
                return Results.Ok(new
                {
                    message = "registration_completed",
                    userId = result.UserId,
                    username = result.Username,
                    role = result.Role
                });
            }
            catch (EmailRegistrationException ex)
            {
                return Results.Json(new { code = ex.Code, message = ex.Message },
                    statusCode: ex.Code == "account_exists" ? 409 : 400);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Results.StatusCode(499);
            }
            catch (Exception)
            {
                return Results.Json(new
                {
                    code = "server_error",
                    message = "Khong xac minh duoc OTP luc nay."
                }, statusCode: 503);
            }
        }).RequireRateLimiting("otp-verify");

        return endpoints;
    }
}
