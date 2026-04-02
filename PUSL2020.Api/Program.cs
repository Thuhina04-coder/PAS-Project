using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PUSL2020.Api.Data;
using PUSL2020.Api.Dtos;
using PUSL2020.Api.Models;
using PUSL2020.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseInMemoryDatabase("PasDb"));

builder.Services.AddSingleton<PasswordHasher<User>>();
builder.Services.AddScoped<TokenService>();

var jwtKey = builder.Configuration["Jwt:Key"];
var jwtIssuer = builder.Configuration["Jwt:Issuer"];
var jwtAudience = builder.Configuration["Jwt:Audience"];

if (string.IsNullOrWhiteSpace(jwtKey))
{
    throw new InvalidOperationException("JWT signing key is missing. Set Jwt:Key in configuration.");
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthentication();
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher<User>>();
    SeedData(db, hasher);
}

app.MapPost("/api/auth/login", async (LoginRequest request, AppDbContext db, TokenService tokens, PasswordHasher<User> hasher) =>
{
    var user = await db.Users.SingleOrDefaultAsync(u => u.Username == request.Username);
    if (user is null)
    {
        return Results.Unauthorized();
    }

    var verify = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
    if (verify == PasswordVerificationResult.Failed)
    {
        return Results.Unauthorized();
    }

    var token = tokens.GenerateToken(user);
    return Results.Ok(new { token, role = user.Role, name = user.FullName });
});

app.MapPost("/api/projects", [Authorize(Roles = "Student")] async (ProjectSubmissionRequest request, ClaimsPrincipal principal, AppDbContext db) =>
{
    var studentId = int.Parse(principal.FindFirstValue("uid")!);

    var researchArea = await db.ResearchAreas.FindAsync(request.ResearchAreaId);
    if (researchArea is null)
    {
        return Results.BadRequest(new { message = "Research area not found." });
    }

    var project = new Project
    {
        Title = request.Title,
        Abstract = request.Abstract,
        TechStack = request.TechStack,
        ResearchAreaId = researchArea.Id,
        StudentId = studentId,
        Status = "Pending"
    };

    db.Projects.Add(project);
    await db.SaveChangesAsync();

    return Results.Created($"/api/projects/{project.Id}", new { project.Id, project.Title, project.Status });
});

app.MapGet("/api/projects/mine", [Authorize(Roles = "Student")] async (ClaimsPrincipal principal, AppDbContext db) =>
{
    var studentId = int.Parse(principal.FindFirstValue("uid")!);

    var projects = await db.Projects
        .Include(p => p.ResearchArea)
        .Where(p => p.StudentId == studentId)
        .Select(p => new
        {
            p.Id,
            p.Title,
            p.Status,
            ResearchArea = p.ResearchArea != null ? p.ResearchArea.Name : string.Empty
        })
        .ToListAsync();

    return Results.Ok(projects);
});

app.MapGet("/api/projects/status", [Authorize(Roles = "Student")] async (ClaimsPrincipal principal, AppDbContext db) =>
{
    var studentId = int.Parse(principal.FindFirstValue("uid")!);

    var statuses = await db.Projects
        .Where(p => p.StudentId == studentId)
        .Select(p => new
        {
            p.Id,
            p.Title,
            p.Status
        })
        .ToListAsync();

    return Results.Ok(statuses);
});

app.MapGet("/api/projects", [Authorize(Roles = "Supervisor,Admin")] async (ClaimsPrincipal principal, AppDbContext db) =>
{
    var role = principal.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
    var userId = int.Parse(principal.FindFirstValue("uid")!);

    IQueryable<Project> query = db.Projects
        .Include(p => p.ResearchArea)
        .Include(p => p.Student);

    if (role == "Supervisor")
    {
        var areaIds = await db.SupervisorResearchAreas
            .Where(sra => sra.UserId == userId)
            .Select(sra => sra.ResearchAreaId)
            .ToListAsync();

        if (areaIds.Count > 0)
        {
            query = query.Where(p => areaIds.Contains(p.ResearchAreaId));
        }
    }

    var list = await query.Select(p => new
    {
        p.Id,
        p.Title,
        p.Status,
        ResearchArea = p.ResearchArea != null ? p.ResearchArea.Name : string.Empty,
        Student = role == "Admin" && p.Student != null ? p.Student.FullName : null
    }).ToListAsync();

    return Results.Ok(list);
});

app.MapPut("/api/supervisors/areas", [Authorize(Roles = "Supervisor")] async (SupervisorAreaSelectionRequest request, ClaimsPrincipal principal, AppDbContext db) =>
{
    var supervisorId = int.Parse(principal.FindFirstValue("uid")!);

    var validAreaIds = await db.ResearchAreas
        .Where(r => request.ResearchAreaIds.Contains(r.Id))
        .Select(r => r.Id)
        .ToListAsync();

    var existing = db.SupervisorResearchAreas.Where(sra => sra.UserId == supervisorId);
    db.SupervisorResearchAreas.RemoveRange(existing);

    foreach (var areaId in validAreaIds)
    {
        db.SupervisorResearchAreas.Add(new SupervisorResearchArea
        {
            UserId = supervisorId,
            ResearchAreaId = areaId
        });
    }

    await db.SaveChangesAsync();
    return Results.Ok(new { count = validAreaIds.Count });
});

app.MapGet("/api/research-areas", [Authorize] async (AppDbContext db) =>
{
    var areas = await db.ResearchAreas
        .OrderBy(r => r.Name)
        .Select(r => new { r.Id, r.Name })
        .ToListAsync();

    return Results.Ok(areas);
});

app.MapPost("/api/research-areas", [Authorize(Roles = "Admin")] async (ResearchAreaRequest request, AppDbContext db) =>
{
    var area = new ResearchArea { Name = request.Name };
    db.ResearchAreas.Add(area);
    await db.SaveChangesAsync();

    return Results.Created($"/api/research-areas/{area.Id}", new { area.Id, area.Name });
});

app.MapPut("/api/research-areas/{id:int}", [Authorize(Roles = "Admin")] async (int id, ResearchAreaRequest request, AppDbContext db) =>
{
    var area = await db.ResearchAreas.FindAsync(id);
    if (area is null)
    {
        return Results.NotFound();
    }

    area.Name = request.Name;
    await db.SaveChangesAsync();

    return Results.Ok(new { area.Id, area.Name });
});

app.MapDelete("/api/research-areas/{id:int}", [Authorize(Roles = "Admin")] async (int id, AppDbContext db) =>
{
    var area = await db.ResearchAreas.FindAsync(id);
    if (area is null)
    {
        return Results.NotFound();
    }

    db.ResearchAreas.Remove(area);
    await db.SaveChangesAsync();

    return Results.NoContent();
});

app.Run();

static void SeedData(AppDbContext db, PasswordHasher<User> hasher)
{
    if (db.Users.Any())
    {
        return;
    }

    var areas = new[]
    {
        new ResearchArea { Name = "Artificial Intelligence" },
        new ResearchArea { Name = "Web Development" },
        new ResearchArea { Name = "Cybersecurity" }
    };

    db.ResearchAreas.AddRange(areas);
    db.SaveChanges();

    var admin = new User { Username = "admin", Role = "Admin", FullName = "Admin User", Email = "admin@example.com" };
    admin.PasswordHash = hasher.HashPassword(admin, "Password123!");

    var supervisor = new User { Username = "supervisor", Role = "Supervisor", FullName = "Dr. Supervisor", Email = "supervisor@example.com" };
    supervisor.PasswordHash = hasher.HashPassword(supervisor, "Password123!");

    var student = new User { Username = "student", Role = "Student", FullName = "Student One", Email = "student@example.com" };
    student.PasswordHash = hasher.HashPassword(student, "Password123!");

    db.Users.AddRange(admin, supervisor, student);
    db.SaveChanges();

    db.SupervisorResearchAreas.Add(new SupervisorResearchArea { UserId = supervisor.Id, ResearchAreaId = areas[0].Id });
    db.SaveChanges();
}
