using System.Threading.Tasks;
using Altinn.Platform.Storage.Authorization;
using Altinn.Platform.Storage.Interface.Models;
using Altinn.Platform.Storage.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Altinn.Platform.Storage.Extensions;

/// <summary>
/// Utility methods for DataType objects.
/// </summary>
internal static class DataTypeExtensions
{
    /// <summary>
    /// Checks if the user has permission to read data of this type for the given storage instance.
    /// </summary>
    public static async Task<bool> CanRead(
        this DataType dataType,
        IAuthorization authorizationService,
        InstanceInternal instance,
        string? task = null
    )
    {
        if (string.IsNullOrWhiteSpace(dataType.ActionRequiredToRead))
        {
            return true;
        }

        return await authorizationService.AuthorizeInstanceAction(
            instance,
            dataType.ActionRequiredToRead,
            task ?? instance.Process?.CurrentTask?.ElementId
        );
    }

    /// <summary>
    /// Checks if the user has permission to write data of this type for the given storage instance.
    /// </summary>
    public static async Task<bool> CanWrite(
        this DataType dataType,
        IAuthorization authorizationService,
        InstanceInternal instance,
        string? task = null
    )
    {
        if (string.IsNullOrWhiteSpace(dataType.ActionRequiredToWrite))
        {
            return true;
        }

        return await authorizationService.AuthorizeInstanceAction(
            instance,
            dataType.ActionRequiredToWrite,
            task ?? instance.Process?.CurrentTask?.ElementId
        );
    }

    /// <summary>
    /// Whether this data type sets <see cref="DataType.TaskIds"/>, so that its elements may only be written in those tasks.
    /// </summary>
    public static bool IsBoundToTasks(this DataType dataType) => dataType.TaskIds is { Count: > 0 };

    /// <summary>
    /// Whether the data type's task binding allows its elements to be written in the given task.
    /// </summary>
    public static bool IsWritableInTask(this DataType dataType, string? task) =>
        !dataType.IsBoundToTasks() || (task is not null && dataType.TaskIds.Contains(task));

    /// <summary>
    /// Returns why data of this type may not be written in the instance's current task, or null when it may.
    /// </summary>
    /// <remarks>
    /// The deletion <see cref="ApplicationLogic.AutoDeleteOnProcessEnd"/> asks for is exempt once the process has ended,
    /// because the app makes it after it has stored the ended process, when the instance has no current task.
    /// </remarks>
    public static ProblemDetails? GetTaskBindingConflict(
        this DataType dataType,
        InstanceInternal instance,
        bool isDelete
    )
    {
        string? currentTask = instance.Process?.CurrentTask?.ElementId;
        if (dataType.IsWritableInTask(currentTask))
        {
            return null;
        }

        if (
            isDelete
            && dataType.AppLogic?.AutoDeleteOnProcessEnd == true
            && instance.Process?.Ended is not null
        )
        {
            return null;
        }

        string boundTasks = string.Join(", ", dataType.TaskIds);
        return new ProblemDetails
        {
            Type = "task_binding_conflict",
            Title = "Task binding conflict",
            Status = StatusCodes.Status409Conflict,
            Detail = currentTask is null
                ? $"Data element of type {dataType.Id} can only be modified in {boundTasks} (no current task)"
                : $"Data element of type {dataType.Id} can only be modified in {boundTasks} (current task {currentTask})",
        };
    }
}
