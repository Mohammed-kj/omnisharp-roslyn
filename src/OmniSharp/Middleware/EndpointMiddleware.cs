using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Composition.Hosting;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNet.Builder;
using Microsoft.AspNet.Http;
using Microsoft.Framework.Logging;
using Newtonsoft.Json;
using OmniSharp.Mef;
using OmniSharp.Middleware.Endpoint;
using OmniSharp.Models;
using OmniSharp.Plugins;
using OmniSharp.Services;

namespace OmniSharp.Middleware
{
    public class EndpointMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly HashSet<string> _endpoints;
        private readonly IReadOnlyDictionary<string, Lazy<EndpointHandler>> _endpointHandlers;
        private readonly LanguagePredicateHandler _languagePredicateHandler;
        private readonly CompositionHost _host;
        private readonly ILogger _logger;
        private readonly IEnumerable<IProjectSystem> _projectSystems;

        public EndpointMiddleware(RequestDelegate next, CompositionHost host, ILoggerFactory loggerFactory, IEnumerable<Endpoints.EndpointMapItem> endpoints)
        {
            _next = next;
            _host = host;
            _projectSystems = host.GetExports<IProjectSystem>();
            _logger = loggerFactory.CreateLogger<EndpointMiddleware>();
            _languagePredicateHandler = new LanguagePredicateHandler(_projectSystems);

            _endpoints = new HashSet<string>(endpoints.Select(x => x.EndpointName).Distinct(), StringComparer.OrdinalIgnoreCase);

            var endpointHandlers = endpoints.ToDictionary(
                x => x.EndpointName,
                endpoint => new Lazy<EndpointHandler>(() => new EndpointHandler(_languagePredicateHandler, _host, _logger, endpoint, Enumerable.Empty<Plugin>())),
                StringComparer.OrdinalIgnoreCase);

            _endpointHandlers = new ReadOnlyDictionary<string, Lazy<EndpointHandler>>(endpointHandlers);
        }

        public Task Invoke(HttpContext httpContext)
        {
            if (httpContext.Request.Path.HasValue)
            {
                var endpoint = httpContext.Request.Path.Value;
                if (_endpoints.Contains(endpoint))
                {
                    Lazy<EndpointHandler> handler;
                    if (_endpointHandlers.TryGetValue(endpoint, out handler))
                    {
                        return handler.Value.Handle(httpContext);
                    }
                }
            }

            return _next(httpContext);
        }
    }

    public static class EndpointMiddlewareExtensions
    {
        public static IApplicationBuilder UseEndpointMiddleware(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<EndpointMiddleware>(OmniSharp.Endpoints.AvailableEndpoints.AsEnumerable());
        }
    }
}
