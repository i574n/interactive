﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Events;
using Microsoft.DotNet.Interactive.Formatting;
using Microsoft.DotNet.Interactive.ValueSharing;

namespace Microsoft.DotNet.Interactive.HttpRequest;

public class HttpRequestKernel :
    Kernel,
    IKernelCommandHandler<RequestValue>,
    IKernelCommandHandler<SendValue>,
    IKernelCommandHandler<SubmitCode>,
    IKernelCommandHandler<RequestDiagnostics>,
    IKernelCommandHandler<RequestValueInfos>
{
    internal const int DefaultResponseDelayThresholdInMilliseconds = 1000;
    internal const int DefaultContentByteLengthThreshold = 500_000;

    private readonly HttpClient _client;
    private readonly int _responseDelayThresholdInMilliseconds;
    private readonly long _contentByteLengthThreshold;

    private readonly Dictionary<string, object> _variables = new(StringComparer.InvariantCultureIgnoreCase);

    public HttpRequestKernel(
        string? name = null,
        HttpClient? client = null,
        int responseDelayThresholdInMilliseconds = DefaultResponseDelayThresholdInMilliseconds,
        int contentByteLengthThreshold = DefaultContentByteLengthThreshold) : base(name ?? "http")
    {
        KernelInfo.LanguageName = "HTTP";
        KernelInfo.DisplayName = $"{KernelInfo.LocalName} - HTTP Request";

        _client = client ?? new HttpClient();
        _responseDelayThresholdInMilliseconds = responseDelayThresholdInMilliseconds;
        _contentByteLengthThreshold = contentByteLengthThreshold;

        RegisterForDisposal(_client);
    }

    Task IKernelCommandHandler<RequestValue>.HandleAsync(RequestValue command, KernelInvocationContext context)
    {
        if (_variables.TryGetValue(command.Name, out var value))
        {
            var valueProduced = new ValueProduced(
                value,
                command.Name,
                FormattedValue.CreateSingleFromObject(value, JsonFormatter.MimeType),
                command);
            context.Publish(valueProduced);
        }
        else
        {
            context.Fail(command, message: $"Value not found: {command.Name}");
        }

        return Task.CompletedTask;
    }

    Task IKernelCommandHandler<RequestValueInfos>.HandleAsync(RequestValueInfos command, KernelInvocationContext context)
    {
        var valueInfos = _variables.Select(v => new KernelValueInfo(v.Key, FormattedValue.CreateSingleFromObject(v.Value, PlainTextSummaryFormatter.MimeType))).ToArray();

        context.Publish(new ValueInfosProduced(valueInfos, command));

        return Task.CompletedTask;
    }

    async Task IKernelCommandHandler<SendValue>.HandleAsync(SendValue command, KernelInvocationContext context)
    {
        await SetValueAsync(command, context, SetValueAsync);
    }

    private Task SetValueAsync(string valueName, object value, Type? declaredType = null)
    {
        _variables[valueName] = value;
        return Task.CompletedTask;
    }

    async Task IKernelCommandHandler<SubmitCode>.HandleAsync(SubmitCode command, KernelInvocationContext context)
    {
        var parseResult = HttpRequestParser.Parse(command.Code);

        var httpRequestResults = parseResult.SyntaxTree.RootNode.ChildNodes.OfType<HttpRequestNode>().Select(n => n.TryGetHttpRequestMessage(BindExpressionValues)).ToArray();

        var requestMessages = httpRequestResults.Select(r => r.Value).ToArray();
        var diagnostics = httpRequestResults.SelectMany(r => r.Diagnostics).ToArray();

        PublishDiagnostics(context, command, diagnostics);

        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            context.Fail(command);
            return;
        }

        try
        {
            foreach (var requestMessage in requestMessages)
            {
                if (requestMessage is not null)
                {
                    await SendRequestAsync(requestMessage, command, context);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            context.Fail(command, message: ex.Message);
        }
    }

    private async Task SendRequestAsync(HttpRequestMessage requestMessage, KernelCommand command, KernelInvocationContext context)
    {
        var cancellationToken = context.CancellationToken;
        var isResponseAvailable = false;
        var semaphore = new SemaphoreSlim(1);
        string? valueId = null;

        try
        {
            await Task.WhenAll(SendRequestAndHandleResponseAsync(), HandleDelayedResponseAsync());
        }
        catch
        {
            ClearDisplayedValue();
            throw;
        }

        async Task HandleDelayedResponseAsync()
        {
            await Task.Delay(_responseDelayThresholdInMilliseconds);

            await semaphore.WaitAsync(cancellationToken);
            try
            {
                if (!isResponseAvailable)
                {
                    var emptyResponse = new EmptyHttpResponse();
                    UpdateDisplayedValue(emptyResponse);
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        async Task SendRequestAndHandleResponseAsync()
        {
            var response = await GetResponseWithTimingAsync(requestMessage, cancellationToken);

            await semaphore.WaitAsync(cancellationToken);
            isResponseAvailable = true;
            semaphore.Release();

            var contentLength = response.Content?.ByteLength ?? 0;
            if (contentLength >= _contentByteLengthThreshold)
            {
                var partialResponse = response.ToPartialHttpResponse();
                UpdateDisplayedValue(partialResponse);
            }

            UpdateDisplayedValue(response);

            var jsonFormattedResponse =
                FormattedValue.CreateSingleFromObject(response, JsonFormatter.MimeType);

            jsonFormattedResponse.SuppressDisplay = true;

            context.Publish(
                new ReturnValueProduced(
                    response,
                    command,
                    formattedValues: new[] { jsonFormattedResponse }));
        }

        void UpdateDisplayedValue(object response)
        {
            var formattedValues = FormattedValue.CreateManyFromObject(response);

            if (string.IsNullOrEmpty(valueId))
            {
                valueId = Guid.NewGuid().ToString();
                context.Publish(new DisplayedValueProduced(response, command, formattedValues, valueId));
            }
            else
            {
                context.Publish(new DisplayedValueUpdated(response, valueId, command, formattedValues));
            }
        }

        void ClearDisplayedValue()
        {
            if (!string.IsNullOrEmpty(valueId))
            {
                var htmlFormattedValue = new FormattedValue(HtmlFormatter.MimeType, "<span/>");
                var plainTextFormattedValue = new FormattedValue(PlainTextFormatter.MimeType, string.Empty);
                var formattedValues = new[] { htmlFormattedValue, plainTextFormattedValue };
                context.Publish(new DisplayedValueUpdated(value: null, valueId, command, formattedValues));
            }
        }
    }

    private async Task<HttpResponse> GetResponseWithTimingAsync(
        HttpRequestMessage requestMessage,
        CancellationToken cancellationToken)
    {
        HttpResponse response;
        var stopWatch = Stopwatch.StartNew();

        try
        {
            var responseMessage = await _client.SendAsync(requestMessage, cancellationToken);
            response = (await responseMessage.ToHttpResponseAsync(cancellationToken))!;
        }
        finally
        {
            stopWatch.Stop();
        }

        response.ElapsedMilliseconds = stopWatch.Elapsed.TotalMilliseconds;
        return response;
    }

    private void PublishDiagnostics(KernelInvocationContext context, KernelCommand command, IReadOnlyCollection<Diagnostic> diagnostics)
    {
        if (diagnostics.Any())
        {
            var formattedDiagnostics =
                diagnostics
                    .Select(d => d.ToString())
                    .Select(text => new FormattedValue(PlainTextFormatter.MimeType, text))
                    .ToArray();

            context.Publish(new DiagnosticsProduced(diagnostics, command, formattedDiagnostics));
        }
    }

    Task IKernelCommandHandler<RequestDiagnostics>.HandleAsync(RequestDiagnostics command, KernelInvocationContext context)
    {
        var parseResult = HttpRequestParser.Parse(command.Code);
        var requestsAndDiagnostics = GetAllDiagnostics(parseResult);
        var diagnostics = requestsAndDiagnostics;
        PublishDiagnostics(context, command, diagnostics);
        return Task.CompletedTask;
    }

    private List<Diagnostic> GetAllDiagnostics(HttpRequestParseResult parseResult)
    {
        var diagnostics = new List<Diagnostic>();

        foreach (var diagnostic in parseResult.GetDiagnostics())
        {
            diagnostics.Add(diagnostic);
        }

        foreach (var expressionNode in parseResult.SyntaxTree.RootNode.DescendantNodesAndTokensAndSelf().OfType<HttpExpressionNode>())
        {
            if (BindExpressionValues(expressionNode) is { IsSuccessful: false } bindResult)
            {
                diagnostics.AddRange(bindResult.Diagnostics);
            }
        }

        return diagnostics;
    }

    private HttpBindingResult<object?> BindExpressionValues(HttpExpressionNode node)
    {
        var variableName = node.Text;
        var expression = variableName;

        if (_variables.TryGetValue(expression, out var value))
        {
            return HttpBindingResult<object?>.Success(value);
        }

        var diagnostic = node.CreateDiagnostic($"Cannot resolve symbol '{variableName}'");

        return HttpBindingResult<object?>.Failure(diagnostic);
    }
}