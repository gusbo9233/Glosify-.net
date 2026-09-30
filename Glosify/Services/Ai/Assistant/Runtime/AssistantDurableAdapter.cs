using Microsoft.Extensions.Options;

namespace Glosify.Services.Ai.Assistant.Runtime;

internal sealed class AssistantDurableAdapter(AssistantTaskStore tasks, IServiceScopeFactory scopes,
    IOptions<AssistantRuntimeOptions> options)
{
    public bool Enabled => options.Value.Enabled;
    public async Task<AssistantTurnResponse> SendAsync(Guid threadId, string userId, AssistantTaskInput input, CancellationToken ct)
    {
        var task = await tasks.StartAsync(threadId, userId, new(Guid.NewGuid().ToString("N"), input), ct, manualApproval: true);
        while (true)
        {
            ct.ThrowIfCancellationRequested(); // Cancels only this legacy HTTP wait.
            await using var scope = scopes.CreateAsyncScope();
            task = await scope.ServiceProvider.GetRequiredService<AssistantTaskStore>().ViewAsync(task.Id, userId, ct);
            if (task.Result is not null) return RuntimeJson.Read<AssistantTurnResponse>(RuntimeJson.Write(task.Result));
            if (!AssistantTaskStore.Runnable(task.Status))
                return new(threadId, Guid.Empty, Guid.Empty, task.Reason ?? "Task paused; saved progress is retained.", [], [], task.Status);
            await Task.Delay(500, ct);
        }
    }
}
