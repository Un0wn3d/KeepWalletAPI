namespace KeepWalletAPI.Contracts;

public record CategoryResponse(
    int Id,
    string Name,
    string Type,
    string IconKey
);

public record CreateCategoryRequest(
    string Name,
    string Type,
    string? IconKey = null
);

public record UpdateCategoryRequest(
    string? Name = null,
    string? IconKey = null
);

public record UserCategoryPreferenceResponse(
    int Id,
    string Name,
    string Type,
    string IconKey,
    bool IsSelected
);

public record UpdateUserCategoryPreferencesRequest(
    IReadOnlyList<int> SelectedCategoryIds
);

public record MergeCategoryRequest(
    int TargetCategoryId
);
