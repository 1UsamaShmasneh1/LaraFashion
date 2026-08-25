namespace LaraFashion.Services;

public sealed class SandboxFilterState
{
    public string? Size { get; private set; }
    public event Func<Task>? Changed;

    public async Task SetSizeAsync(string size)
    {
        Size = size;
        await NotifyChangedAsync();
    }

    public async Task ClearSizeAsync()
    {
        if (Size is null)
            return;

        Size = null;
        await NotifyChangedAsync();
    }

    private async Task NotifyChangedAsync()
    {
        if (Changed is null)
            return;

        foreach (var handler in Changed.GetInvocationList().Cast<Func<Task>>())
            await handler();
    }
}
