using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Mycroft.Api.Contracts;
using Mycroft.Core.Entities;
using Mycroft.Infrastructure.Persistence;

namespace Mycroft.Api.Endpoints;

public static class CategoryEndpoints
{
    public static IEndpointRouteBuilder MapCategoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/categories").WithTags("Categories");

        group.MapGet("/", async (MycroftDbContext db, CancellationToken ct) =>
                (await db.Categories.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct))
                .Select(ToResponse)
                .ToList())
            .WithName("ListCategories")
            .WithSummary("Default taxonomy used when a job has no customCategories");

        group.MapPost("/", CreateAsync)
            .WithName("CreateCategory")
            .WithSummary("Add a category to the default taxonomy");

        return app;
    }

    private static async Task<Results<Created<CategoryResponse>, Conflict<string>, ValidationProblem>> CreateAsync(
        CategoryRequest request, MycroftDbContext db, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        var subs = (request.Subcategories ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();

        if (name.Length is 0 or > 100)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["name is required (max 100 characters)."] });
        if (subs.Any(s => s.Length > 100))
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["subcategories"] = ["Subcategories must have at most 100 characters."] });
        if (await db.Categories.AnyAsync(c => c.Name == name, ct))
            return TypedResults.Conflict($"Category '{name}' already exists.");

        var category = new Category { Name = name, Subcategories = subs, IsSystem = false };
        db.Categories.Add(category);
        await db.SaveChangesAsync(ct);

        return TypedResults.Created($"/api/v1/categories/{category.Id}", ToResponse(category));
    }

    private static CategoryResponse ToResponse(Category c) => new(c.Id, c.Name, c.Subcategories, c.IsSystem);
}
