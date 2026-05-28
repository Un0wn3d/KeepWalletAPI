using NpgsqlTypes;

namespace KeepWalletAPI.Models;

public enum CategoryType
{
    [PgName("income")]
    Income,

    [PgName("expense")]
    Expense
}

public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public CategoryType Type { get; set; }
    public string? IconKey { get; set; } = "other";
    public string? Color { get; set; }
}
