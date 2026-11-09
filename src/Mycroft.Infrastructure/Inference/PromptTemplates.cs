using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mycroft.Core.Enums;
using Mycroft.Core.Models;

namespace Mycroft.Infrastructure.Inference;

internal static class PromptTemplates
{
    // Relaxed escaping keeps accents readable for the model ("SÃO" instead of "SÃO").
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string SystemPrompt(IReadOnlyList<TaxonomyEntry> taxonomy)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Você é um motor de reconciliação financeira brasileiro. Para cada transação bruta (extrato bancário, fatura de cartão, CNAB, PDF) extraia dados estruturados.");
        sb.AppendLine("Regras:");
        sb.AppendLine("- Responda SOMENTE com JSON válido no schema fornecido, com exatamente um item por lineIndex recebido.");
        sb.AppendLine("- Analise CADA transação de forma independente, olhando apenas a sua própria descrição. Descrições diferentes quase nunca têm o mesmo merchant.");
        // No concrete example on purpose: small models copy any example ("iFood", "Loja Exemplo") into unrelated lines.
        sb.AppendLine("- merchantName: nome do estabelecimento ou da pessoa que aparece na PRÓPRIA descrição, limpo e legível, sem prefixos de operação (PIX, PAY, PGTO, COMPRA), códigos, datas ou cidades. Não acrescente palavras que não estão na descrição.");
        sb.AppendLine("- Se a descrição não identifica um estabelecimento conhecido, NÃO invente: use o texto limpo da descrição como merchantName e confidenceScore abaixo de 0.6.");
        sb.AppendLine("- Pistas comuns em extratos brasileiros (use se a categoria existir na taxonomia): \"PIX TRANSF <nome>\"/\"TED\"/\"DOC\" = transferência para pessoa; \"IOF\", \"JUROS\", \"TARIFA\" = encargos bancários; \"FATURA PAGA\"/\"PAG FATURA\" = pagamento de fatura de cartão; \"PAGTO SALARIO\" = salário; \"REND PAGO\" = rendimento de aplicação; \"DEV PIX\"/\"ESTORNO\" = devolução; \"PAY <nome>\" ou \"COMPRA CARTAO <nome>\" = compra no estabelecimento <nome> com cartão de débito (NÃO é tarifa bancária).");
        sb.AppendLine("- amount: valor numérico com ponto decimal; NEGATIVO para débitos/compras/saídas e POSITIVO para créditos/entradas. Se 'amount' foi informado na entrada, repita-o.");
        sb.AppendLine("- transactionDate: formato ISO yyyy-MM-dd, ou null se desconhecida. Se 'date' foi informada, repita-a.");
        sb.AppendLine("- transactionType: \"Debit\" ou \"Credit\".");
        sb.AppendLine($"- paymentMethod: um de {string.Join(", ", Enum.GetNames<PaymentMethod>())}.");
        sb.AppendLine("- installmentCurrent/installmentTotal: parcelas (ex: \"PARC 02/10\" -> 2 e 10), senão null.");
        sb.AppendLine("- isRecurring: true para assinaturas/contas mensais (streaming, academia, energia, aluguel).");
        sb.AppendLine("- confidenceScore: entre 0 e 1, sua confiança na categoria e no merchant. Use valores acima de 0.9 apenas quando o estabelecimento for inequívoco.");
        sb.AppendLine("- Se 'hint' estiver presente, ele é determinístico: use exatamente seu merchantName/category/subcategory.");
        sb.AppendLine("- category e subcategory DEVEM ser escolhidas exclusivamente da taxonomia abaixo:");
        foreach (var t in taxonomy)
            sb.AppendLine($"  * {t.Name}: {string.Join(" | ", t.Subcategories)}");
        return sb.ToString();
    }

    public static string UserPrompt(IEnumerable<InferenceRequestItem> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            var node = new JsonObject
            {
                ["lineIndex"] = item.Line.LineIndex,
                ["description"] = item.Line.RawDescription,
            };
            if (item.Line.RawAmount is { } amount) node["amount"] = amount;
            if (item.Line.RawDate is { } date) node["date"] = date.ToString("yyyy-MM-dd");
            if (item.Hint is { } hint)
            {
                node["hint"] = new JsonObject
                {
                    ["merchantName"] = hint.MerchantName,
                    ["category"] = hint.Category,
                    ["subcategory"] = hint.Subcategory,
                };
            }
            array.Add(node);
        }

        return "Transações:\n" + array.ToJsonString(Compact);
    }

    public static string RepairPrompt(string validationError) =>
        $"Sua resposta anterior foi rejeitada pela validação: {validationError}\nCorrija e responda novamente apenas com o JSON válido, um item por lineIndex.";

    /// <summary>
    /// Compact, strictly typed JSON schema. Category is constrained to the job taxonomy through an enum, which
    /// Ollama compiles into the sampling grammar — the model physically cannot emit an unknown category.
    /// </summary>
    public static JsonElement BuildSchema(IReadOnlyList<TaxonomyEntry> taxonomy)
    {
        static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
        static JsonObject Nullable(string type) => new() { ["type"] = new JsonArray("null", type) };

        var subcategories = taxonomy.SelectMany(t => t.Subcategories).Distinct().ToList();
        var subcategorySchema = subcategories.Count > 0
            ? new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "string", ["enum"] = Strings(subcategories) }, new JsonObject { ["type"] = "null" }) }
            : Nullable("string");

        var item = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["lineIndex"] = new JsonObject { ["type"] = "integer" },
                ["merchantName"] = new JsonObject { ["type"] = "string" },
                ["amount"] = Nullable("number"),
                ["currency"] = new JsonObject { ["type"] = "string" },
                ["transactionDate"] = Nullable("string"),
                ["transactionType"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(Enum.GetNames<TransactionType>()) },
                ["category"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(taxonomy.Select(t => t.Name)) },
                ["subcategory"] = subcategorySchema,
                ["paymentMethod"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(Enum.GetNames<PaymentMethod>()) },
                ["installmentCurrent"] = Nullable("integer"),
                ["installmentTotal"] = Nullable("integer"),
                ["isRecurring"] = new JsonObject { ["type"] = "boolean" },
                ["confidenceScore"] = new JsonObject { ["type"] = "number" },
            },
            ["required"] = Strings(["lineIndex", "merchantName", "amount", "currency", "transactionDate", "transactionType", "category", "subcategory", "paymentMethod", "installmentCurrent", "installmentTotal", "isRecurring", "confidenceScore"]),
        };

        var root = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["items"] = new JsonObject { ["type"] = "array", ["items"] = item } },
            ["required"] = new JsonArray("items"),
        };

        return JsonSerializer.SerializeToElement(root, Compact);
    }
}
