using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
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
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the JWT from POST /api/auth/login (Swagger adds the Bearer prefix)."
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

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
    var username = request.Username.Trim();
    var password = request.Password;

    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
    {
        return Results.BadRequest(new { message = "Username and password are required." });
    }

    var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Username == username);
    if (user is null)
    {
        return Results.Unauthorized();
    }

    var verify = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
    if (verify == PasswordVerificationResult.Failed)
    {
        return Results.Unauthorized();
    }

    var token = tokens.GenerateToken(user);
    return Results.Ok(new
    {
        token,
        tokenType = "Bearer",
        expiresInMinutes = 240,
        role = user.Role,
        name = user.FullName
    });
});

app.MapPost("/api/projects", [Authorize(Roles = "Student")] async (ProjectSubmissionRequest request, ClaimsPrincipal principal, AppDbContext db) =>
{
    var studentId = int.Parse(principal.FindFirstValue("uid")!);
    var title = request.Title.Trim();
    var abstractText = request.Abstract.Trim();
    var techStack = request.TechStack.Trim();

    if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(abstractText) || string.IsNullOrWhiteSpace(techStack))
    {
        return Results.BadRequest(new { message = "Title, abstract, and tech stack are required." });
    }

    var researchArea = await db.ResearchAreas.FindAsync(request.ResearchAreaId);
    if (researchArea is null)
    {
        return Results.BadRequest(new { message = "Research area not found." });
    }

    var normalizedTitle = title.ToLowerInvariant();
    var hasDuplicatePending = await db.Projects
        .AnyAsync(p => p.StudentId == studentId && p.Status == "Pending" && p.Title.ToLower() == normalizedTitle);

    if (hasDuplicatePending)
    {
        return Results.Conflict(new { message = "You already have a pending project with the same title." });
    }

    var project = new Project
    {
        Title = title,
        Abstract = abstractText,
        TechStack = techStack,
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
        .AsNoTracking()
        .Include(p => p.ResearchArea)
        .Where(p => p.StudentId == studentId)
        .OrderByDescending(p => p.Id)
        .Select(p => new
        {
            p.Id,
            p.Title,
            p.Status,
            ResearchArea = p.ResearchArea != null ? p.ResearchArea.Name : string.Empty
        })
        .ToListAsync();

    return Results.Ok(new { total = projects.Count, items = projects });
});

app.MapGet("/api/projects", [Authorize(Roles = "Supervisor,Admin")] async (ClaimsPrincipal principal, string? status, int? researchAreaId, string? title, AppDbContext db) =>
{
    var role = principal.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
    var userId = int.Parse(principal.FindFirstValue("uid")!);

    IQueryable<Project> query = db.Projects
        .AsNoTracking()
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

    if (!string.IsNullOrWhiteSpace(status))
    {
        var normalizedStatus = status.Trim();
        query = query.Where(p => p.Status == normalizedStatus);
    }

    if (researchAreaId.HasValue)
    {
        query = query.Where(p => p.ResearchAreaId == researchAreaId.Value);
    }

    if (!string.IsNullOrWhiteSpace(title))
    {
        var normalizedTitleFilter = title.Trim().ToLowerInvariant();
        query = query.Where(p => p.Title.ToLower().Contains(normalizedTitleFilter));
    }

    var list = await query
    .OrderByDescending(p => p.Id)
    .Select(p => new
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
    var requestedAreaIds = request.ResearchAreaIds.Distinct().ToList();

    var validAreaIds = await db.ResearchAreas
        .Where(r => requestedAreaIds.Contains(r.Id))
        .Select(r => r.Id)
        .ToListAsync();

    var invalidAreaIds = requestedAreaIds
        .Except(validAreaIds)
        .ToList();

    if (invalidAreaIds.Count > 0)
    {
        return Results.BadRequest(new { message = "One or more research area ids are invalid.", invalidAreaIds });
    }

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
    return Results.Ok(new { count = validAreaIds.Count, researchAreaIds = validAreaIds });
});

app.MapGet("/api/research-areas", [Authorize] async (string? q, AppDbContext db) =>
{
    var query = db.ResearchAreas.AsNoTracking().AsQueryable();

    if (!string.IsNullOrWhiteSpace(q))
    {
        var normalized = q.Trim().ToLowerInvariant();
        query = query.Where(r => r.Name.ToLower().Contains(normalized));
    }

    var areas = await query
        .OrderBy(r => r.Name)
        .Select(r => new
        {
            r.Id,
            r.Name,
            ProjectCount = r.Projects.Count,
            SupervisorCount = r.SupervisorResearchAreas.Count
        })
        .ToListAsync();

    return Results.Ok(areas);
});

app.MapPost("/api/research-areas", [Authorize(Roles = "Admin")] async (ResearchAreaRequest request, AppDbContext db) =>
{
    var name = request.Name.Trim();
    if (string.IsNullOrWhiteSpace(name))
    {
        return Results.BadRequest(new { message = "Research area name is required." });
    }

    var exists = await db.ResearchAreas.AnyAsync(r => r.Name.ToLower() == name.ToLower());
    if (exists)
    {
        return Results.Conflict(new { message = "Research area already exists." });
    }

    var area = new ResearchArea { Name = name };
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

    var name = request.Name.Trim();
    if (string.IsNullOrWhiteSpace(name))
    {
        return Results.BadRequest(new { message = "Research area name is required." });
    }

    var exists = await db.ResearchAreas.AnyAsync(r => r.Id != id && r.Name.ToLower() == name.ToLower());
    if (exists)
    {
        return Results.Conflict(new { message = "Another research area already uses this name." });
    }

    area.Name = name;
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

    var inUseByProjects = await db.Projects.AnyAsync(p => p.ResearchAreaId == id);
    if (inUseByProjects)
    {
        return Results.Conflict(new { message = "Research area cannot be deleted while projects are assigned to it." });
    }

    var areaSelections = db.SupervisorResearchAreas.Where(sra => sra.ResearchAreaId == id);
    db.SupervisorResearchAreas.RemoveRange(areaSelections);

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
