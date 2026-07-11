using System.Security.Claims;

namespace Recall.Web.Middleware;

public class DevAuthMiddleware(RequestDelegate next, ILogger<DevAuthMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
#if DEBUG
        if (context.Request.Path.Value!.Contains('.'))
        {
            // Ignore for static file requests
            await next(context);
            return;
        }
        
        if (!context.User.Identity?.IsAuthenticated ?? true)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, "1"), // or a real test user ID
                new(ClaimTypes.Name, "dev-user"),
                new(ClaimTypes.Email, "dev@example.com")
            };

            var identity = new ClaimsIdentity(claims, "DevAuth");
            context.User = new ClaimsPrincipal(identity);

            logger.LogInformation("Injected development user.");
        }
#endif
        await next(context);
    }
}