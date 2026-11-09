using Mycroft.Core.Models;

namespace Mycroft.Api.Contracts;

internal static class RequestValidation
{
    public static Dictionary<string, string[]> ValidateBatch(BatchReconciliationRequest request, int maxItems)
    {
        var errors = new Dictionary<string, string[]>();

        if (request.Transactions is null || request.Transactions.Count == 0)
            errors["transactions"] = ["At least one transaction is required."];
        else if (request.Transactions.Count > maxItems)
            errors["transactions"] = [$"A job accepts at most {maxItems} transactions (MAX_ITEMS_PER_JOB)."];
        else
        {
            for (var i = 0; i < request.Transactions.Count; i++)
            {
                var t = request.Transactions[i];
                if (string.IsNullOrWhiteSpace(t.RawDescription))
                    errors[$"transactions[{i}].rawDescription"] = ["rawDescription is required."];
                else if (t.RawDescription.Length > 2000)
                    errors[$"transactions[{i}].rawDescription"] = ["rawDescription must have at most 2000 characters."];
                if (t.ExternalLineId is { Length: > 100 })
                    errors[$"transactions[{i}].externalLineId"] = ["externalLineId must have at most 100 characters."];
            }
        }

        ValidateCommon(request.ExternalReferenceId, request.CallbackUrl, request.WebhookSecret, request.CustomCategories, errors);
        return errors;
    }

    public static void ValidateCommon(string? externalReferenceId, string? callbackUrl, string? webhookSecret,
        IReadOnlyList<CustomCategory>? categories, Dictionary<string, string[]> errors)
    {
        if (externalReferenceId is { Length: > 120 })
            errors["externalReferenceId"] = ["externalReferenceId must have at most 120 characters."];

        if (callbackUrl is not null &&
            (callbackUrl.Length > 500 || !Uri.TryCreate(callbackUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            errors["callbackUrl"] = ["callbackUrl must be an absolute http(s) URL with at most 500 characters."];

        if (webhookSecret is { Length: > 255 })
            errors["webhookSecret"] = ["webhookSecret must have at most 255 characters."];

        if (categories is null) return;
        for (var i = 0; i < categories.Count; i++)
        {
            var c = categories[i];
            if (string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 100)
                errors[$"customCategories[{i}].name"] = ["Category name is required (max 100 characters)."];
            for (var j = 0; j < c.Aliases.Count; j++)
            {
                var a = c.Aliases[j];
                if (string.IsNullOrWhiteSpace(a.Pattern?.Replace("%", "")))
                    errors[$"customCategories[{i}].aliases[{j}].pattern"] = ["Alias pattern must contain text besides wildcards."];
                if (a.Subcategory is not null && c.Subcategories.Count > 0 && !c.Subcategories.Contains(a.Subcategory))
                    errors[$"customCategories[{i}].aliases[{j}].subcategory"] = [$"Subcategory '{a.Subcategory}' is not listed in category '{c.Name}'."];
            }
        }

        var duplicated = categories.GroupBy(c => c.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicated.Count > 0) errors["customCategories"] = [$"Duplicated category names: {string.Join(", ", duplicated)}."];
    }
}
