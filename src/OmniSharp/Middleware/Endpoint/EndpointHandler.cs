using System;
using System.Collections.Generic;
using System.Composition.Hosting;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNet.Http;
using Microsoft.Framework.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OmniSharp.Mef;
using OmniSharp.Middleware.Endpoint.Exports;
using OmniSharp.Models;
using OmniSharp.Plugins;

namespace OmniSharp.Middleware.Endpoint
{
    class LanguageModel
    {
        public string Language { get; set; }
        public string FileName { get; set; }
    }

    class EndpointHandler
    {
        private static readonly MethodInfo GetDelegateExportsMethod = typeof(EndpointHandler)
            .GetTypeInfo()
            .DeclaredMethods
            .Single(methodInfo => methodInfo.Name == nameof(GetDelegateExports));

        private static readonly MethodInfo GetRequestHandlerExportsMethod = typeof(EndpointHandler)
            .GetTypeInfo()
            .DeclaredMethods
            .Single(methodInfo => methodInfo.Name == nameof(GetRequestHandlerExports));

        private static Type TaskType = typeof(Task<>);
        private static Type FuncType = typeof(Func<,>);
        private static Type RequestHandlerType = typeof(RequestHandler<,>);

        private readonly CompositionHost _host;
        private readonly Type _delegateType;
        private readonly Type _requestHandlerType;
        private readonly LanguagePredicateHandler _languagePredicateHandler;
        private readonly Lazy<Task<Dictionary<string, ExportHandler>>> _exports;
        private readonly Type _requestType;
        private readonly Type _responseType;
        private readonly bool _hasLanguageProperty;
        private readonly bool _hasFileNameProperty;
        private readonly bool _isMergeable;
        private readonly bool _isSpreadable;
        private readonly ILogger _logger;
        private readonly IEnumerable<Plugin> _plugins;

        public EndpointHandler(LanguagePredicateHandler languagePredicateHandler, CompositionHost host, ILogger logger, OmniSharp.Endpoints.EndpointMapItem item, IEnumerable<Plugin> plugins)
        {
            EndpointName = item.EndpointName;
            _host = host;
            _logger = logger;
            _languagePredicateHandler = languagePredicateHandler;
            _plugins = plugins;

            _delegateType = FuncType.MakeGenericType(item.RequestType, TaskType.MakeGenericType(item.ResponseType));
            _requestHandlerType = RequestHandlerType.MakeGenericType(item.RequestType, item.ResponseType);
            _requestType = item.RequestType;
            _responseType = item.ResponseType;

            _hasLanguageProperty = item.RequestType.GetRuntimeProperty(nameof(LanguageModel.Language)) != null;
            _hasFileNameProperty = item.RequestType.GetRuntimeProperty(nameof(Request.FileName)) != null;
            _isMergeable = typeof(IMergeableResponse).IsAssignableFrom(item.ResponseType);
            _isSpreadable = _isMergeable || item.TakeOne;

            _exports = new Lazy<Task<Dictionary<string, ExportHandler>>>(() => LoadExportHandlers());
        }

        private Task<Dictionary<string, ExportHandler>> LoadExportHandlers()
        {
            var delegateExports = (IEnumerable<ExportHandler>)GetDelegateExportsMethod.MakeGenericMethod(_delegateType).Invoke(this, new object[] { });
            var interfaceExports = (IEnumerable<ExportHandler>)GetRequestHandlerExportsMethod.MakeGenericMethod(_requestHandlerType).Invoke(this, new object[] { });

            var plugins = _plugins.Where(x => x.Config.Endpoints.Contains(EndpointName))
                .Select(plugin => new PluginExportHandler(EndpointName, plugin, _responseType));

            return Task.FromResult(delegateExports
               .Concat(interfaceExports)
               .Concat(plugins)
               .ToDictionary(export => export.Language));
        }

        public string EndpointName { get; }

        public Task Handle(HttpContext context)
        {
            var request = DeserializeRequestObject(context.Request.Body);
            var model = GetLanguageModel(request);
            if (_hasLanguageProperty)
            {
                return HandleLanguageRequest(model.Language, request, context);
            }

            if (_hasFileNameProperty)
            {
                var language = _languagePredicateHandler.GetLanguageForFilePath(model.FileName);
                return HandleLanguageRequest(language, request, context);
            }

            return HandleAllRequest(request, context);
        }

        private Task HandleLanguageRequest(string language, JObject requestObject, HttpContext context)
        {
            if (!string.IsNullOrEmpty(language))
            {
                return HandleSingleRequest(language, requestObject, context);
            }

            return HandleAllRequest(requestObject, context);
        }

        private async Task HandleSingleRequest(string language, JObject requestObject, HttpContext context)
        {
            var request = requestObject.ToObject(_requestType);
            var exports = await _exports.Value;
            ExportHandler handler;
            if (exports.TryGetValue(language, out handler))
            {
                var response = await handler.Handle(request);
                SerializeResponseObject(context.Response, response);
                return;
            }

            throw new NotSupportedException($"{language} does not support {EndpointName}");
        }

        private async Task HandleAllRequest(JObject requestObject, HttpContext context)
        {
            if (!_isSpreadable)
            {
                throw new NotSupportedException($"Responses must be mergable to spread them out across all plugins for {EndpointName}");
            }

            var exports = await _exports.Value;
            var request = requestObject.ToObject(_requestType);

            object response = null;

            if (_isMergeable)
            {
                IMergeableResponse mergableResponse = null;

                var responses = new List<Task<object>>();
                foreach (var handler in exports.Values)
                {
                    responses.Add(handler.Handle(request));
                }

                foreach (IMergeableResponse exportResponse in await Task.WhenAll(responses))
                {
                    if (mergableResponse != null)
                    {
                        mergableResponse = mergableResponse.Merge(exportResponse);
                    }
                    else
                    {
                        mergableResponse = exportResponse;
                    }
                }

                response = mergableResponse;
            }
            else
            {
                foreach (var handler in exports.Values)
                {
                    response = await handler.Handle(request);

                    if (response != null)
                        break;
                }
            }

            if (response != null)
            {
                SerializeResponseObject(context.Response, response);
            }
        }

        private LanguageModel GetLanguageModel(JObject jobject)
        {
            var response = new LanguageModel();
            JToken token;
            if (jobject.TryGetValue(nameof(LanguageModel.Language), StringComparison.OrdinalIgnoreCase, out token))
            {
                response.Language = token.ToString();
            }


            if (jobject.TryGetValue(nameof(LanguageModel.FileName), StringComparison.OrdinalIgnoreCase, out token))
            {
                response.FileName = token.ToString();
            }

            return response;
        }

        private JObject DeserializeRequestObject(Stream readStream)
        {
            return JObject.Load(new JsonTextReader(new StreamReader(readStream)));
        }

        private void SerializeResponseObject(HttpResponse response, object value)
        {
            using (var writer = new StreamWriter(response.Body))
            {
                using (var jsonWriter = new JsonTextWriter(writer))
                {
                    jsonWriter.CloseOutput = false;
                    var jsonSerializer = JsonSerializer.Create(/*TODO: SerializerSettings*/);
                    jsonSerializer.Serialize(jsonWriter, value);
                }
            }
        }

        private IEnumerable<ExportHandler> GetRequestHandlerExports<T>()
        {
            var typeInfo = typeof(T).GetTypeInfo();
            var exports = _host.GetExports<Lazy<T, OmniSharpLanguage>>();
            foreach (var export in exports)
            {
                var genericType = typeof(RequestHandlerExportHandler<,>).MakeGenericType(_requestType, _responseType);
                yield return (ExportHandler)Activator.CreateInstance(genericType, export.Metadata.Language, export.Value);
            }
        }

        private IEnumerable<ExportHandler> GetDelegateExports<T>()
        {
            var typeInfo = typeof(T).GetTypeInfo();
            var exports = _host.GetExports<Lazy<T, OmniSharpLanguage>>();
            foreach (var export in exports)
            {
                var genericType = typeof(DelegateExportHandler<,>).MakeGenericType(_requestType, _responseType);
                yield return (ExportHandler)Activator.CreateInstance(genericType, export.Metadata.Language, export.Value);
            }
        }
    }
}