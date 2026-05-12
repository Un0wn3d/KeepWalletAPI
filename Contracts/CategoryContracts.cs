namespace KeepWalletAPI.Contracts;

public record CategoryResponse(
    int Id,
    string Name,
    string Type
);

public record CreateCategoryRequest(
    string Name,
    string Type
);

public record UserCategoryPreferenceResponse(
    int Id,
    string Name,
    string Type,
    bool IsSelected
);

public record UpdateUserCategoryPreferencesRequest(
    IReadOnlyList<int> SelectedCategoryIds
);

public record MergeCategoryRequest(
    int TargetCategoryId
);
