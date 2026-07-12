using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Middleware;

public class DevAuthMiddleware(RequestDelegate next, ILogger<DevAuthMiddleware> logger)
{
    private static readonly Guid FixedDevUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public async Task InvokeAsync(HttpContext context, AppDbContext dbContext)
    {
#if DEBUG
        if (context.Request.Path.Value?.Contains('.') == true)
        {
            await next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            // Ensure dev user exists in DB so app flows using AppUserEntity work
            var devUser = await dbContext.Set<AppUserEntity>()
                .FirstOrDefaultAsync(u => u.Id == FixedDevUserId);

            if (devUser is null)
            {
                devUser = new AppUserEntity
                {
                    Id = FixedDevUserId,
                    UserId = string.Empty, // keep for now if you plan Identity later
                    Username = "dev-user",
                    Email = "dev@example.com",
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                };

                dbContext.Add(devUser);
                await dbContext.SaveChangesAsync();
            }

            var claims = new List<Claim>
            {
                // IMPORTANT: Guid as string
                new(ClaimTypes.NameIdentifier, devUser.Id.ToString()),
                new(ClaimTypes.Name, devUser.Username),
                new(ClaimTypes.Email, devUser.Email),
            };

            var identity = new ClaimsIdentity(claims, "DevAuth");
            context.User = new ClaimsPrincipal(identity);

            logger.LogInformation("Injected development user {UserId}", devUser.Id);
        }
#endif
        await next(context);
    }
}