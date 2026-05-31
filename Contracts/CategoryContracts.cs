namespace KeepWalletAPI.Contracts;

public record CategoryResponse(
    int Id,
    string Name,
    string Type,
    string IconKey,
    string? Color = null
);

public record CreateCategoryRequest(
    string Name,
    string Type,
    string? IconKey = null,
    string? Color = null
);

public record UpdateCategoryRequest(
    string? Name = null,
    string? IconKey = null,
    string? Color = null
);

public record UserCategoryPreferenceResponse(
    int Id,
    string Name,
    string Type,
    string IconKey,
    string? Color,
    bool IsSelected
);

public record UpdateUserCategoryPreferencesRequest(
    IReadOnlyList<int> SelectedCategoryIds,
    IReadOnlyList<UserCategoryPreferenceUpdate>? Preferences = null
);

public record UserCategoryPreferenceUpdate(
    int CategoryId,
    string? IconKey = null,
    string? Color = null,
    bool? IsActive = null
);

public record MergeCategoryRequest(
    int TargetCategoryId
);
