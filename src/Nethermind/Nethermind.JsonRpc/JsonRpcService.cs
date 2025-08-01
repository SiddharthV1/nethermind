// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Threading;
using Nethermind.JsonRpc.Exceptions;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.State;
using static Nethermind.JsonRpc.Modules.RpcModuleProvider;
using static Nethermind.JsonRpc.Modules.RpcModuleProvider.ResolvedMethodInfo;

namespace Nethermind.JsonRpc;

public class JsonRpcService : IJsonRpcService
{
    private readonly ILogger _logger;
    private readonly IRpcModuleProvider _rpcModuleProvider;
    private readonly HashSet<string> _methodsLoggingFiltering;
    private readonly Lock _propertyInfoModificationLock = new();
    private readonly int _maxLoggedRequestParametersCharacters;

    private Dictionary<TypeAsKey, PropertyInfo?> _propertyInfoCache = [];

    public JsonRpcService(IRpcModuleProvider rpcModuleProvider, ILogManager logManager, IJsonRpcConfig jsonRpcConfig)
    {
        _logger = logManager.GetClassLogger<JsonRpcService>();
        _rpcModuleProvider = rpcModuleProvider;
        _methodsLoggingFiltering = (jsonRpcConfig.MethodsLoggingFiltering ?? []).ToHashSet();
        _maxLoggedRequestParametersCharacters = jsonRpcConfig.MaxLoggedRequestParametersCharacters ?? int.MaxValue;
    }

    public async Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest rpcRequest, JsonRpcContext context)
    {
        (int? errorCode, string errorMessage) = Validate(rpcRequest, context);
        if (errorCode.HasValue)
        {
            if (_logger.IsDebug) _logger.Debug($"Validation error when handling request: {rpcRequest}");
            return GetErrorResponse(rpcRequest.Method, errorCode.Value, errorMessage, null, rpcRequest.Id);
        }

        Exception error;
        try
        {
            return await ExecuteRequestAsync(rpcRequest, context);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        return ReturnErrorResponse(rpcRequest, error);
    }

    private JsonRpcErrorResponse ReturnErrorResponse(JsonRpcRequest rpcRequest, Exception ex)
    {
        int errorCode;
        string errorText;
        if (ex is TargetInvocationException tx)
        {
            errorCode = ErrorCodes.InternalError;
            ex = tx.InnerException;
            errorText = "Internal error";
        }
        else if (ex is LimitExceededException)
        {
            errorCode = ErrorCodes.LimitExceeded;
            errorText = "Too many requests";
        }
        else if (ex is ModuleRentalTimeoutException)
        {
            errorCode = ErrorCodes.ModuleTimeout;
            errorText = "Timeout";
        }
        else
        {
            if (_logger.IsError) _logger.Error($"Error during validation, request: {rpcRequest}", ex);
            return GetErrorResponse(ErrorCodes.ParseError, "Parse error", rpcRequest.Id, rpcRequest.Method);
        }

        if (_logger.IsError) _logger.Error($"Error during method execution, request: {rpcRequest}", ex);
        return GetErrorResponse(rpcRequest.Method, errorCode, errorText, ex.ToString(), rpcRequest.Id);
    }

    private Task<JsonRpcResponse> ExecuteRequestAsync(JsonRpcRequest rpcRequest, JsonRpcContext context)
    {
        string methodName = rpcRequest.Method.Trim();

        ResolvedMethodInfo? result = _rpcModuleProvider.Resolve(methodName);
        return result?.MethodInfo is not null
            ? ExecuteAsync(rpcRequest, methodName, result, context)
            : Task.FromResult<JsonRpcResponse>(GetErrorResponse(methodName, ErrorCodes.MethodNotFound, "Method not found", $"{rpcRequest.Method}", rpcRequest.Id));
    }

    private async Task<JsonRpcResponse> ExecuteAsync(JsonRpcRequest request, string methodName, ResolvedMethodInfo method, JsonRpcContext context)
    {
        JsonElement providedParameters = request.Params;

        if (_logger.IsTrace) LogRequest(methodName, providedParameters, method.ExpectedParameters);

        var providedParametersLength = providedParameters.ValueKind == JsonValueKind.Array ? providedParameters.GetArrayLength() : 0;
        int missingParamsCount = method.ExpectedParameters.Length - providedParametersLength;
        int initialMissingParamsCount = missingParamsCount;

        if (providedParametersLength > 0)
        {
            foreach (JsonElement item in providedParameters.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Null || (item.ValueKind == JsonValueKind.String && item.ValueEquals(ReadOnlySpan<byte>.Empty)))
                {
                    missingParamsCount++;
                }
                else
                {
                    missingParamsCount = initialMissingParamsCount;
                }
            }
        }

        int explicitNullableParamsCount = 0;

        if (missingParamsCount != 0)
        {
            bool hasIncorrectParameters = true;
            if (missingParamsCount > 0)
            {
                hasIncorrectParameters = false;
                for (int i = 0; i < missingParamsCount; i++)
                {
                    int parameterIndex = method.ExpectedParameters.Length - missingParamsCount + i;
                    bool nullable = method.ExpectedParameters[parameterIndex].IsNullable;

                    // if the null is the default parameter it could be passed in an explicit way as "" or null
                    // or we can treat null as a missing parameter. Two tests for this cases:
                    // Eth_call_is_working_with_implicit_null_as_the_last_argument and Eth_call_is_working_with_explicit_null_as_the_last_argument
                    bool isExplicit = providedParametersLength >= parameterIndex + 1;
                    if (nullable && isExplicit)
                    {
                        explicitNullableParamsCount += 1;
                    }
                    if (!method.ExpectedParameters[method.ExpectedParameters.Length - missingParamsCount + i].IsOptional && !nullable)
                    {
                        hasIncorrectParameters = true;
                        break;
                    }
                }
            }

            if (hasIncorrectParameters)
            {
                return GetErrorResponse(methodName, ErrorCodes.InvalidParams, "Invalid params", $"Incorrect parameters count, expected: {method.ExpectedParameters.Length}, actual: {method.ExpectedParameters.Length - missingParamsCount}", request.Id);
            }
        }

        missingParamsCount -= explicitNullableParamsCount;

        //prepare parameters
        object[]? parameters = null;
        bool hasMissing = false;
        if (method.ExpectedParameters.Length > 0)
        {
            (parameters, hasMissing) = DeserializeParameters(method.ExpectedParameters, providedParametersLength, providedParameters, missingParamsCount);
            if (parameters is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Incorrect JSON RPC parameters when calling {methodName} with params [{string.Join(", ", providedParameters)}]");
                return GetErrorResponse(methodName, ErrorCodes.InvalidParams, "Invalid params", null, request.Id);
            }
        }

        //execute method
        IResultWrapper resultWrapper = null;
        IRpcModule rpcModule = await _rpcModuleProvider.Rent(methodName, method.ReadOnly);
        if (rpcModule is IContextAwareRpcModule contextAwareModule)
        {
            contextAwareModule.Context = context;
        }
        bool returnImmediately = methodName != "eth_getLogs";
        Action? returnAction = returnImmediately ? null : () => _rpcModuleProvider.Return(methodName, rpcModule);
        try
        {
            object invocationResult = hasMissing ?
                method.MethodInfo.Invoke(rpcModule, parameters) :
                method.Invoker.Invoke(rpcModule, new Span<object?>(parameters));

            switch (invocationResult)
            {
                case IResultWrapper wrapper:
                    resultWrapper = wrapper;
                    break;
                case Task task:
                    await task;
                    resultWrapper = GetResultProperty(task)?.GetValue(task) as IResultWrapper;
                    break;
            }
        }
        catch (Exception e) when (e is TargetParameterCountException || e is ArgumentException)
        {
            return GetErrorResponse(methodName, ErrorCodes.InvalidParams, e.Message, e.ToString(), request.Id, returnAction);
        }
        catch (TargetInvocationException e) when (e.InnerException is JsonException)
        {
            return GetErrorResponse(methodName, ErrorCodes.InvalidParams, "Invalid params", e.InnerException?.ToString(), request.Id, returnAction);
        }
        catch (Exception e) when (e is OperationCanceledException || e.InnerException is OperationCanceledException)
        {
            string errorMessage = $"{methodName} request was canceled due to enabled timeout.";
            return GetErrorResponse(methodName, ErrorCodes.Timeout, errorMessage, null, request.Id, returnAction);
        }
        catch (Exception e) when (e.InnerException is InsufficientBalanceException)
        {
            return GetErrorResponse(methodName, ErrorCodes.InvalidInput, e.InnerException.Message, e.ToString(), request.Id, returnAction);
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error($"Error during method execution, request: {request}", ex);
            return GetErrorResponse(methodName, ErrorCodes.InternalError, "Internal error", ex.ToString(), request.Id, returnAction);
        }
        finally
        {
            if (returnImmediately)
            {
                _rpcModuleProvider.Return(methodName, rpcModule);
            }
        }

        if (resultWrapper is null)
        {
            string errorMessage = $"Method {methodName} execution result does not implement IResultWrapper";
            if (_logger.IsError) _logger.Error(errorMessage);
            return GetErrorResponse(methodName, ErrorCodes.InternalError, errorMessage, null, request.Id, returnAction);
        }

        Result? result = resultWrapper.Result;

        return result.ResultType != ResultType.Success
            ? GetErrorResponse(methodName, resultWrapper.ErrorCode, result.Error, resultWrapper.Data, request.Id, returnAction, resultWrapper.IsTemporary)
            : GetSuccessResponse(methodName, resultWrapper.Data, request.Id, returnAction);
    }

    private PropertyInfo? GetResultProperty(Task task)
    {
        Type type = task.GetType();
        if (_propertyInfoCache.TryGetValue(type, out PropertyInfo? value))
        {
            return value;
        }

        return GetResultPropertySlow(type);
    }

    private PropertyInfo? GetResultPropertySlow(Type type)
    {
        lock (_propertyInfoModificationLock)
        {
            Dictionary<TypeAsKey, PropertyInfo?> current = _propertyInfoCache;
            // Re-check inside the lock in case another thread already added it
            if (current.TryGetValue(type, out PropertyInfo? value))
            {
                return value;
            }

            // Copy-on-write: create a new dictionary so we don't mutate
            // the one other threads may be reading without locks.
            Dictionary<TypeAsKey, PropertyInfo?> propertyInfoCache = new(current);
            PropertyInfo? propertyInfo = type.GetProperty("Result");
            propertyInfoCache[type] = propertyInfo;

            // Publish the new cache instance atomically by swapping the reference.
            // Readers grabbing _propertyInfoCache will now see the updated dictionary.
            _propertyInfoCache = propertyInfoCache;

            return propertyInfo;
        }
    }

    private void LogRequest(string methodName, JsonElement providedParameters, ExpectedParameter[] expectedParameters)
    {
        if (_logger.IsTrace && !_methodsLoggingFiltering.Contains(methodName))
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("Executing JSON RPC call ");
            builder.Append(methodName);
            builder.Append(" with params [");

            int paramsLength = 0;
            int paramsCount = 0;
            const string separator = ", ";

            if (providedParameters.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement param in providedParameters.EnumerateArray())
                {
                    string? parameter = expectedParameters.ElementAtOrDefault(paramsCount).Info?.Name == "passphrase"
                        ? "{passphrase}"
                        : param.GetRawText();

                    if (paramsLength > _maxLoggedRequestParametersCharacters)
                    {
                        int toRemove = paramsLength - _maxLoggedRequestParametersCharacters;
                        builder.Remove(builder.Length - toRemove, toRemove);
                        builder.Append("...");
                        break;
                    }

                    if (paramsCount != 0)
                    {
                        builder.Append(separator);
                        paramsLength += separator.Length;
                    }

                    builder.Append(parameter);
                    paramsLength += (parameter?.Length ?? 0);
                    paramsCount++;
                }
            }
            builder.Append(']');
            string log = builder.ToString();
            _logger.Trace(log);
        }
    }

    private static object? DeserializeParameter(JsonElement providedParameter, ExpectedParameter expectedParameter)
    {
        Type paramType = expectedParameter.Info.ParameterType;
        if (paramType.IsByRef)
        {
            paramType = paramType.GetElementType();
        }

        if (providedParameter.ValueKind == JsonValueKind.Null || (providedParameter.ValueKind == JsonValueKind.String && providedParameter.ValueEquals(ReadOnlySpan<byte>.Empty)))
        {
            return providedParameter.ValueKind == JsonValueKind.Null && expectedParameter.IsNullable
                ? null
                : Type.Missing;
        }

        object? executionParam;
        if (paramType == typeof(string))
        {
            executionParam = providedParameter.ValueKind == JsonValueKind.String ?
                providedParameter.GetString() :
                providedParameter.GetRawText();
        }
        else if (expectedParameter.IsIJsonRpcParam)
        {
            IJsonRpcParam jsonRpcParam = expectedParameter.CreateRpcParam();
            jsonRpcParam!.ReadJson(providedParameter, EthereumJsonSerializer.JsonOptions);
            executionParam = jsonRpcParam;
        }
        else
        {
            if (providedParameter.ValueKind == JsonValueKind.String)
            {
                JsonConverter converter = EthereumJsonSerializer.JsonOptions.GetConverter(paramType);
                executionParam = converter.GetType().Namespace.StartsWith("System.", StringComparison.Ordinal)
                    ? JsonSerializer.Deserialize(providedParameter.GetString(), paramType, EthereumJsonSerializer.JsonOptions)
                    : providedParameter.Deserialize(paramType, EthereumJsonSerializer.JsonOptions);
            }
            else
            {
                executionParam = providedParameter.Deserialize(paramType, EthereumJsonSerializer.JsonOptions);
            }
        }

        return executionParam;
    }

    private (object[]? parameters, bool hasMissing) DeserializeParameters(
        ExpectedParameter[] expectedParameters,
        int providedParametersLength,
        JsonElement providedParameters,
        int missingParamsCount)
    {
        const int parallelThreshold = 4;
        try
        {
            bool hasMissing = false;
            int totalLength = providedParametersLength + missingParamsCount;

            if (totalLength == 0) return (Array.Empty<object>(), false);

            object[] executionParameters = new object[totalLength];

            if (providedParametersLength <= parallelThreshold)
            {
                for (int i = 0; i < providedParametersLength; i++)
                {
                    JsonElement providedParameter = providedParameters[i];
                    ExpectedParameter expectedParameter = expectedParameters[i];

                    object? parameter = DeserializeParameter(providedParameter, expectedParameter);
                    executionParameters[i] = parameter;
                    if (!hasMissing && ReferenceEquals(parameter, Type.Missing))
                    {
                        hasMissing = true;
                    }
                }
            }
            else if (providedParametersLength > parallelThreshold)
            {
                ParallelUnbalancedWork.For(
                    0,
                    providedParametersLength,
                    ParallelUnbalancedWork.DefaultOptions,
                    (providedParameters, expectedParameters, executionParameters, hasMissing),
                    static (i, state) =>
                {
                    JsonElement providedParameter = state.providedParameters[i];
                    ExpectedParameter expectedParameter = state.expectedParameters[i];

                    object? parameter = DeserializeParameter(providedParameter, expectedParameter);
                    state.executionParameters[i] = parameter;
                    if (!state.hasMissing && ReferenceEquals(parameter, Type.Missing))
                    {
                        state.hasMissing = true;
                    }

                    return state;
                });
            }

            for (int i = providedParametersLength; i < totalLength; i++)
            {
                executionParameters[i] = Type.Missing;
            }
            hasMissing |= providedParametersLength < totalLength;
            return (executionParameters, hasMissing);
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn("Error while parsing JSON RPC request parameters " + e);
            return (null, false);
        }
    }

    private static JsonRpcResponse GetSuccessResponse(string methodName, object result, object id, Action? disposableAction)
    {
        JsonRpcResponse response = new JsonRpcSuccessResponse(disposableAction)
        {
            Result = result,
            Id = id,
            MethodName = methodName
        };

        return response;
    }

    public JsonRpcErrorResponse GetErrorResponse(int errorCode, string errorMessage, object? id = null, string? methodName = null) =>
        GetErrorResponse(methodName ?? string.Empty, errorCode, errorMessage, null, id);

    private JsonRpcErrorResponse GetErrorResponse(
        string methodName,
        int errorCode,
        string? errorMessage,
        object? errorData,
        object? id,
        Action? disposableAction = null,
        bool suppressWarning = false)
    {
        if (_logger.IsDebug) _logger.Debug($"Sending error response, method: {(string.IsNullOrEmpty(methodName) ? "none" : methodName)}, id: {id}, errorType: {errorCode}, message: {errorMessage}, errorData: {errorData}");
        JsonRpcErrorResponse response = new(disposableAction)
        {
            Error = new Error
            {
                Code = errorCode,
                Message = errorMessage,
                Data = errorData,
                SuppressWarning = suppressWarning
            },
            Id = id,
            MethodName = methodName
        };

        return response;
    }

    private (int? ErrorType, string ErrorMessage) Validate(JsonRpcRequest? rpcRequest, JsonRpcContext context)
    {
        if (rpcRequest is null)
        {
            return (ErrorCodes.InvalidRequest, "Invalid request");
        }

        string methodName = rpcRequest.Method;
        if (string.IsNullOrWhiteSpace(methodName))
        {
            return (ErrorCodes.InvalidRequest, "Method is required");
        }

        methodName = methodName.Trim();

        ModuleResolution result = _rpcModuleProvider.Check(methodName, context, out string? module);
        if (result == ModuleResolution.Enabled)
        {
            return (null, null);
        }

        return GetErrorResult(methodName, context, result, module);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static (int? ErrorType, string ErrorMessage) GetErrorResult(string methodName, JsonRpcContext context, ModuleResolution result, string module)
        {
            return result switch
            {
                ModuleResolution.Unknown => (ErrorCodes.MethodNotFound, $"The method '{methodName}' is not supported."),
                ModuleResolution.Disabled => (ErrorCodes.InvalidRequest,
                    $"The method '{methodName}' is found but the namespace '{module}' is disabled for {context.Url?.ToString() ?? "n/a"}. Consider adding the namespace '{module}' to JsonRpc.AdditionalRpcUrls for an additional URL, or to JsonRpc.EnabledModules for the default URL."),
                ModuleResolution.EndpointDisabled => (ErrorCodes.InvalidRequest,
                    $"The method '{methodName}' is found in namespace '{module}' for {context.Url?.ToString() ?? "n/a"}' but is disabled for {context.RpcEndpoint}."),
                ModuleResolution.NotAuthenticated => (ErrorCodes.InvalidRequest, $"The method '{methodName}' must be authenticated."),
                _ => (null, null)
            };
        }
    }
}
