namespace AngleSharp
{
    using AngleSharp.Browser;
    using AngleSharp.Browser.Dom;
    using AngleSharp.Dom;
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// A simple and lightweight browsing context.
    /// </summary>
    public sealed class BrowsingContext : EventTarget, IBrowsingContext, IDisposable
    {
        #region Fields

        private readonly IEnumerable<Object> _originalServices;
        private readonly List<Object> _services;
        private readonly Sandboxes _security;
        private readonly IBrowsingContext? _parent;
        private readonly IDocument? _creator;
        private readonly Boolean _isFrameContext;
        private readonly IHistory? _history;
        private readonly Dictionary<String, WeakReference<IBrowsingContext>> _children;
        private readonly List<WeakReference<IBrowsingContext>> _contextGroup;

        #endregion

        #region ctor

        /// <summary>
        /// Creates a new browsing context with the given configuration, or the
        /// default configuration, if no configuration is provided.
        /// </summary>
        /// <remarks>
        /// This constructor was only added due to PowerShell. See #844 for details.
        /// </remarks>
        /// <param name="configuration">The optional configuration.</param>
        public BrowsingContext(IConfiguration? configuration = null)
            : this((configuration ?? AngleSharp.Configuration.Default).Services, Sandboxes.None)
        {
        }

        private BrowsingContext(Sandboxes security)
        {
            _services = [];
            _originalServices = _services;
            _security = security;
            _children = [];
            _contextGroup = [];
        }

        internal BrowsingContext(IEnumerable<Object> services, Sandboxes security)
            : this(security)
        {
            _services.AddRange(services);
            _originalServices = services;
            _history = GetService<IHistory>();
        }

        internal BrowsingContext(IBrowsingContext parent, Sandboxes security, Boolean isFrameContext)
            : this(parent.OriginalServices, security)
        {
            _parent = parent;
            _creator = _parent.Active;
            _isFrameContext = isFrameContext;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets or sets the currently active document.
        /// </summary>
        public IDocument? Active
        {
            get;
            set;
        }

        /// <summary>
        /// Gets the document that created the current context, if any. The
        /// creator is the active document of the parent at the time of
        /// creation.
        /// </summary>
        public IDocument? Creator => _creator;

        /// <summary>
        /// Gets the original services for the given browsing context.
        /// </summary>
        public IEnumerable<Object> OriginalServices => _originalServices;

        /// <summary>
        /// Gets the current window proxy.
        /// </summary>
        public IWindow? Current => Active?.DefaultView;

        /// <summary>
        /// Gets the parent of the current context, if any. If a parent is
        /// available, then the current context contains only embedded
        /// documents.
        /// </summary>
        public IBrowsingContext? Parent => _parent;

        /// <summary>
        /// Determines if the current context is for a frame rather than
        /// a window. Useful for properly determining the proper target
        /// for `_top` and `_parent`.
        ///</summary>
        internal Boolean IsFrame => _isFrameContext;

        /// <summary>
        /// Gets the session history of the given browsing context, if any.
        /// </summary>
        public IHistory? SessionHistory => _history;

        /// <summary>
        /// Gets the sandboxing flag of the context.
        /// </summary>
        public Sandboxes Security => _security;

        #endregion

        #region Methods

        /// <summary>
        /// Gets an instance of the given service.
        /// </summary>
        /// <typeparam name="T">The type of service to resolve.</typeparam>
        /// <returns>The instance of the service or null.</returns>
        public T? GetService<T>() where T : class
        {
            var count = _services.Count;

            for (var i = 0; i < count; i++)
            {
                var service = _services[i];
                var instance = service as T;

                if (instance is null)
                {
                    if (service is Func<IBrowsingContext, T> creator)
                    {
                        instance = creator.Invoke(this);
                        _services[i] = instance;
                    }
                    else
                    {
                        continue;
                    }
                }

                return instance;
            }

            return null;
        }

        /// <summary>
        /// Gets all registered instances of the given service.
        /// </summary>
        /// <typeparam name="T">The type of service to resolve.</typeparam>
        /// <returns>An enumerable with all service instances.</returns>
        public IEnumerable<T> GetServices<T>() where T : class
        {
            var count = _services.Count;

            for (var i = 0; i < count; i++)
            {
                var service = _services[i];
                var instance = service as T;

                if (instance is null)
                {
                    if (service is Func<IBrowsingContext, T> creator)
                    {
                        instance = creator.Invoke(this);
                        _services[i] = instance;
                    }
                    else
                    {
                        continue;
                    }
                }

                yield return instance;
            }
        }

        /// <summary>
        /// Creates a new named browsing context as child of the given parent.
        /// </summary>
        /// <param name="name">The name of the child context, if any.</param>
        /// <param name="security">The security flags to apply.</param>
        /// <returns></returns>
        public IBrowsingContext CreateChild(String? name, Sandboxes security)
        {
            return CreateChild(name, security, false);
        }

        /// <summary>
        /// Creates a new named browsing context as child of the given parent.
        /// </summary>
        /// <param name="name">The name of the child context, if any.</param>
        /// <param name="security">The security flags to apply.</param>
        /// <param name="isFrameContext">Whether the child context is for a frame.</param>
        /// <returns></returns>
        internal IBrowsingContext CreateChild(String? name, Sandboxes security, Boolean isFrameContext)
        {
            var context = new BrowsingContext(this, security, isFrameContext);

            // if the new context is not a frame context, then it should be added
            // to the top-most browsing context, as a new top-level auxilary
            // browser context
            if (!isFrameContext)
            {
                if (_contextGroup is null)
                {
                    // _parent should not be null if _contextGroup is null
                    return _parent!.CreateChild(name, security);
                }
                _contextGroup.Add(new(context));
            }

            if (name is { Length: > 0 })
            {
                _children[name] = new WeakReference<IBrowsingContext>(context);
            }

            return context;
        }

        /// <summary>
        /// Finds a named browsing context.
        /// </summary>
        /// <param name="name">The name of the browsing context.</param>
        /// <returns>The found instance, if any.</returns>
        public IBrowsingContext? FindChild(String name)
        {
            var excludedChild = default(IBrowsingContext);
            var currentContext = this;
            var foundChildContext = default(IBrowsingContext);
            while (foundChildContext is null && currentContext is not null)
            {
                foundChildContext = currentContext.FindChildRecursive(name, excludedChild);
                excludedChild = currentContext;
                currentContext = currentContext.Parent as BrowsingContext;
            }

            if (foundChildContext is null && excludedChild is BrowsingContext { _contextGroup : not null and  var group })
            {
                // TODO
                // if the initial browsing context was part of a top-level auxilary browsing context,
                // it should be filtered out so that it is not searched again
                foreach (var contextRef in group)
                {
                    if (!contextRef.TryGetTarget(out var c) || c is not BrowsingContext context)
                    {
                        continue;
                    }

                    foundChildContext = context.FindChildRecursive(name, null);

                    if (foundChildContext is not null) {
                        return foundChildContext;
                    }
                }
            }

            return foundChildContext;
        }

        private IBrowsingContext? FindChildRecursive(String name, IBrowsingContext? excludedContext)
        {
            var context = default(IBrowsingContext);

            if (!String.IsNullOrEmpty(name) && _children.TryGetValue(name, out var reference))
            {
                reference.TryGetTarget(out context);
            }

            if (context is null && Active is Document active)
            {
                foreach (var childContext in active.GetAttachedReferences<BrowsingContext>())
                {
                    if (childContext.Equals(excludedContext))
                    {
                        continue;
                    }
                    context = childContext.FindChildRecursive(name, null);
                    if (context is not null)
                    {
                        break;
                    }
                }
            }

            return context;
        }

        /// <summary>
        /// Creates a new browsing context with the given configuration, or the
        /// default configuration, if no configuration is provided.
        /// </summary>
        /// <param name="configuration">The optional configuration.</param>
        /// <returns>The browsing context to use.</returns>
        public static IBrowsingContext New(IConfiguration? configuration = null)
        {
            configuration ??= AngleSharp.Configuration.Default;

            return new BrowsingContext(configuration.Services, Sandboxes.None);
        }

        /// <summary>
        /// Creates a new browsing context from the given service.
        /// </summary>
        /// <param name="instance">The service instance.</param>
        /// <returns>The browsing context to use.</returns>
        public static IBrowsingContext NewFrom<TService>(TService instance)
        {
            var configuration = Configuration.Default.WithOnly<TService>(instance);
            return new BrowsingContext(configuration.Services, Sandboxes.None);
        }

        void IDisposable.Dispose()
        {
            Active?.Dispose();
            Active = null;
        }

        #endregion
    }
}
