﻿using Rhetos.Dom.DefaultConcepts;
using Rhetos.Logging;
using Rhetos.Security;
using Rhetos.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.ServiceModel.Activation;
using System.ServiceModel.Web;
using System.Text;
using System.Web;
using System.Web.Security;
using Newtonsoft.Json;
using Rhetos.WindowsAuthImpersonation.Abstractions;

namespace Rhetos.WindowsAuthImpersonation
{
    #region Service parameters

    public class ImpersonateParameters
    {
        public string ImpersonatedUser { get; set; }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(ImpersonatedUser))
                throw new UserException("Empty ImpersonatedUser is not allowed.");
        }
    }

    #endregion

    [ServiceContract]
    [AspNetCompatibilityRequirements(RequirementsMode = AspNetCompatibilityRequirementsMode.Required)]
    public class ImpersonationService
    {
        public static readonly string ImpersonatingUserInfoPrefix = "Impersonating:";
        public static readonly Claim IncreasePermissionsClaim = new Claim("WindowsAuthImpersonation.Impersonate", "IncreasePermissions");
        public static readonly string[] SupportedAuthenticationTypes = {"Negotiate", "Windows", "Kerberos"};

        private readonly ILogger _logger;
        private readonly Lazy<IAuthorizationManager> _authorizationManager;
        private readonly Lazy<GenericRepository<IPrincipal>> _principals;
        private readonly Lazy<GenericRepository<ICommonClaim>> _claims;
        private readonly Lazy<IAuthorizationProvider> _authorizationProvider;
        private readonly IHttpContextAccessor _httpContextAccessor;

        private readonly Lazy<FormsAuthenticationTicket> _authenticationTicket;

        public ImpersonationService(
            ILogProvider logProvider,
            Lazy<IAuthorizationManager> authorizationManager,
            Lazy<GenericRepository<IPrincipal>> principals,
            Lazy<GenericRepository<ICommonClaim>> claims,
            Lazy<IAuthorizationProvider> authorizationProvider,
            IHttpContextAccessor httpContextAccessor)
        {
            _logger = logProvider.GetLogger(GetType().Name);

            _logger.Info(() => "New instance of ImpersonationService created.");

            _authorizationManager = authorizationManager;
            _principals = principals;
            _claims = claims;
            _authorizationProvider = authorizationProvider;
            _httpContextAccessor = httpContextAccessor;

            _authenticationTicket = new Lazy<FormsAuthenticationTicket>(GetOrCreateTicket);
        }

        #region Service HttpMethods

        [OperationContract]
        [WebInvoke(Method = "GET", UriTemplate = "/Test", BodyStyle = WebMessageBodyStyle.Bare, RequestFormat = WebMessageFormat.Json, ResponseFormat = WebMessageFormat.Xml)]
        public string Test()
        {
            var cookieValue = _httpContextAccessor.HttpContext.Request.Cookies[FormsAuthentication.FormsCookieName]?.Value;
            var response = new
            {
                ticket = cookieValue == null ? null : FormsAuthentication.Decrypt(cookieValue),
                authType = _httpContextAccessor.HttpContext.User.Identity.AuthenticationType,
                userType = _httpContextAccessor.HttpContext.User.GetType().FullName
            };
            _logger.Error(() => "Fake error message to test user context.");
            return JsonConvert.SerializeObject(response, Formatting.Indented);
        }


        [OperationContract]
        [WebInvoke(Method = "POST", UriTemplate = "/Impersonate", BodyStyle = WebMessageBodyStyle.Bare, RequestFormat = WebMessageFormat.Json, ResponseFormat = WebMessageFormat.Json)]
        public void Impersonate(ImpersonateParameters parameters)
        {
            if (parameters == null)
                throw new ClientException("It is not allowed to call this service method with no parameters provided.");

            _logger.Trace(() => "Impersonate: " + _httpContextAccessor.HttpContext.User.Identity.Name + " as " + parameters.ImpersonatedUser);
            parameters.Validate();

            var impersonatedUserName = GetImpersonatedUserName();
            if (impersonatedUserName != null)
                throw new ClientException($"Unable to start impersonation. Already impersonating user '{impersonatedUserName}'. Stop impersonation first.");

            CheckImpersonatedUserPermissions(parameters.ImpersonatedUser);
            SetImpersonatedUser(parameters.ImpersonatedUser);
        }

        [OperationContract]
        [WebInvoke(Method = "POST", UriTemplate = "/StopImpersonating", BodyStyle = WebMessageBodyStyle.Bare, RequestFormat = WebMessageFormat.Json, ResponseFormat = WebMessageFormat.Json)]
        public void StopImpersonating()
        {
            var impersonatedUser = GetImpersonatedUserName();
            _logger.Trace(() => $"StopImpersonating: {GetActualUserName()} as {impersonatedUser}.");

            if (string.IsNullOrEmpty(impersonatedUser)) return;
            SetImpersonatedUser(null);
        }
        #endregion

        #region Public service methods

        public string GetImpersonatedUserName()
        {
            var userData = _authenticationTicket.Value.UserData;

            if (!string.IsNullOrEmpty(userData) && !userData.StartsWith(ImpersonatingUserInfoPrefix))
                throw new FrameworkException("Login impersonation plugin is not supported (" + GetType().FullName + "). The authentication ticket already has the UserData property set.");

            return string.IsNullOrEmpty(userData) ? null : userData.Substring(ImpersonatingUserInfoPrefix.Length);
        }

        public string GetActualUserName()
        {
            var identity = _httpContextAccessor.HttpContext?.User?.Identity;
            if (identity?.IsAuthenticated != true || string.IsNullOrEmpty(identity?.Name))
                throw new FrameworkException("WindowsAuthImpersonation plugin does not support unauthenticated requests.");

            var type = _httpContextAccessor.HttpContext?.User?.Identity?.AuthenticationType;
            if (!SupportedAuthenticationTypes.Contains(type))
                throw new FrameworkException($"WindowsAuthImpersonation plugin does not support AuthenticationType '{type}'.");

            return identity.Name;
        }

        #endregion

        class TempUserInfo : IUserInfo
        {
            public string UserName { get; set; }
            public string Workstation { get; set; }
            public bool IsUserRecognized => true;
            public string Report() { return UserName; }
        }
        /// <summary>
        /// A user with this claim is allowed to impersonate another user that has more permissions.
        /// </summary>
        private void CheckImpersonatedUserPermissions(string impersonatedUser)
        {
            var impersonatedPrincipalId = _principals.Value
                .Query(p => p.Name == impersonatedUser)
                .Select(p => p.ID).SingleOrDefault();

            // This function must be called after the user is authenticated and authorized (see CheckCurrentUserClaim),
            // otherwise the provided error information would be a security issue.
            if (impersonatedPrincipalId == default(Guid))
                throw new UserException("User '{0}' is not registered.", new[] { impersonatedUser }, null, null);

            var allowIncreasePermissions = _authorizationManager.Value.GetAuthorizations(new[] { IncreasePermissionsClaim }).Single();
            if (allowIncreasePermissions) return;

            // The impersonatedUser must have subset of permissions of the impersonating user.
            // It is not allowed to impersonate a user with more permissions then the impersonating user.
            var allClaims = _claims.Value.Query().Where(c => c.Active.Value)
                .Select(c => new { c.ClaimResource, c.ClaimRight }).ToList()
                .Select(c => new Claim(c.ClaimResource, c.ClaimRight)).ToList();

            var impersonatedUserInfo = new TempUserInfo { UserName = impersonatedUser };
            var impersonatedUserClaims = _authorizationProvider.Value.GetAuthorizations(impersonatedUserInfo, allClaims)
                .Zip(allClaims, (hasClaim, claim) => new { hasClaim, claim })
                .Where(c => c.hasClaim).Select(c => c.claim).ToList();

            var actualUserInfo = new TempUserInfo() {UserName = GetActualUserName()};
            var surplusImpersonatedClaims = _authorizationProvider.Value.GetAuthorizations(actualUserInfo, impersonatedUserClaims)
                .Zip(impersonatedUserClaims, (hasClaim, claim) => new { hasClaim, claim })
                .Where(c => !c.hasClaim).Select(c => c.claim).ToList();

            if (!surplusImpersonatedClaims.Any()) return;

            _logger.Info(
                "User '{0}' is not allowed to impersonate '{1}' because the impersonated user has {2} more security claims (for example '{3}'). Increase the user's permissions or add '{4}' security claim.",
                GetActualUserName(),
                impersonatedUser,
                surplusImpersonatedClaims.Count,
                surplusImpersonatedClaims.First().FullName,
                IncreasePermissionsClaim.FullName);

            throw new UserException("You are not allowed to impersonate user '{0}'.",
                new[] { impersonatedUser }, "See server log for more information.", null);
        }

        #region Cookies and Tickets

        private FormsAuthenticationTicket GetOrCreateTicket()
        {
            var actualUserName = GetActualUserName();

            var httpContext = _httpContextAccessor.HttpContext;
            var authenticationCookie = httpContext.Request.Cookies[FormsAuthentication.FormsCookieName];
            if (authenticationCookie != null)
            {
                var decryptedTicked = FormsAuthentication.Decrypt(authenticationCookie.Value);
                if (IsTicketValid(decryptedTicked)) return decryptedTicked;
            }

            // ticket not found or not valid, we will create a fresh one
            return new FormsAuthenticationTicket(actualUserName, false, 1);
        }

        private bool IsTicketValid(FormsAuthenticationTicket authenticationTicket)
        {
            return !authenticationTicket.Expired
                   && authenticationTicket.Name == GetActualUserName();
        }

        private void SetImpersonatedUser(string impersonatedUser)
        {
            var newTicket = new FormsAuthenticationTicket(
                _authenticationTicket.Value.Version,
                _authenticationTicket.Value.Name,
                _authenticationTicket.Value.IssueDate,
                _authenticationTicket.Value.Expiration,
                false,
                impersonatedUser == null ? "" : ImpersonatingUserInfoPrefix + impersonatedUser,
                _authenticationTicket.Value.CookiePath);

            AddResponseCookie(newTicket);
        }

        private void AddResponseCookie(FormsAuthenticationTicket authenticationTicket)
        {
            var httpContext = _httpContextAccessor.HttpContext;
            var authenticationCookie = new HttpCookie(FormsAuthentication.FormsCookieName, FormsAuthentication.Encrypt(authenticationTicket))
            {
                Expires = default(DateTime)
            };
            httpContext.Response.Cookies.Add(authenticationCookie);
        }

        #endregion
    }
}
