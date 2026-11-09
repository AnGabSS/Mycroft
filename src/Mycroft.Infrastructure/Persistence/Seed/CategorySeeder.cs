using Microsoft.EntityFrameworkCore;
using Mycroft.Core.Entities;

namespace Mycroft.Infrastructure.Persistence.Seed;

public static class CategorySeeder
{
    public const string FallbackCategory = "Outros";
    public const string FallbackSubcategory = "Não Identificado";

    public static readonly IReadOnlyList<(string Name, string[] Subcategories)> Defaults =
    [
        ("Alimentação", ["Supermercado", "Restaurante", "Delivery", "Padaria/Café"]),
        ("Transporte", ["App de Mobilidade", "Combustível", "Transporte Público", "Estacionamento/Pedágio"]),
        ("Moradia & Contas", ["Aluguel/Condomínio", "Energia/Água/Gás", "Internet/Telefone", "Manutenção"]),
        ("Saúde & Bem-Estar", ["Farmácia", "Consulta/Exames", "Plano de Saúde", "Academia"]),
        ("Assinaturas & Serviços Digitais", ["Streaming", "Software/Cloud", "Jogos", "Clubes"]),
        ("Compras & Lazer", ["Vestuário", "Eletrônicos", "Entretenimento", "Viagens"]),
        ("Educação", ["Cursos", "Livros", "Mensalidade Escolar"]),
        ("Serviços Financeiros & Impostos", ["Tarifas Bancárias", "IOF/Juros", "Impostos", "Seguros"]),
        ("Transferências & Pix", ["Transferência entre Contas", "Pix Enviado", "Pix Recebido"]),
        ("Salário & Receitas", ["Salário", "Rendimentos", "Reembolso", "Vendas"]),
        (FallbackCategory, [FallbackSubcategory, "Diversos"]),
    ];

    /// <summary>Inserts missing default categories. Idempotent.</summary>
    public static async Task SeedAsync(MycroftDbContext db, CancellationToken ct = default)
    {
        var existing = await db.Categories.Select(c => c.Name).ToListAsync(ct);
        var missing = Defaults.Where(d => !existing.Contains(d.Name)).ToList();
        if (missing.Count == 0) return;

        foreach (var (name, subs) in missing)
            db.Categories.Add(new Category { Name = name, Subcategories = [.. subs], IsSystem = true });

        await db.SaveChangesAsync(ct);
    }
}
