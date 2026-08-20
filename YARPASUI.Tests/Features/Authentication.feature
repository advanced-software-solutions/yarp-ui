Feature: Sign-in protection
  Every page and API of the management UI sits behind a cookie sign-in using credentials
  from YarpUi:Auth. These scenarios protect the full flow: anonymous access is redirected,
  API calls get a plain 401, the login form requires its antiforgery token, credentials are
  checked, sessions are issued and revoked, and return URLs cannot leave the site.

  Background:
    Given a running standalone YARP UI app configured with
      """
      {
        "YarpUi": {
          "DataDirectory": "__DATA_DIR__",
          "Auth": { "Username": "admin", "Password": "correct-password" }
        },
        "ReverseProxy": {
          "Routes": {
            "api": { "ClusterId": "apiCluster", "Match": { "Path": "/api/{**catch-all}" } }
          },
          "Clusters": {
            "apiCluster": { "Destinations": { "primary": { "Address": "https://api.example.com" } } }
          }
        }
      }
      """

  Scenario: Anonymous visitors are redirected to the login page
    When I open the home page
    Then the response redirects to the login page

  Scenario: Anonymous API calls get a plain 401 without a redirect
    When I GET "/api/yarp/config"
    Then the response status is 401

  Scenario: The login page is publicly reachable and renders the sign-in form
    When I GET "/login"
    Then the response status is 200
    And the login page shows the sign-in form

  Scenario: A login post without the antiforgery token is rejected
    When I submit the login form with valid credentials but no antiforgery token
    Then the response status is 400
    And no UI session cookie is issued

  Scenario: Wrong credentials are rejected
    When I submit the login form with username "admin" and password "wrong-password"
    Then the response status is 200
    And the login page shows the message "Invalid username or password."
    And no UI session cookie is issued

  Scenario: Wrong username is rejected
    When I submit the login form with username "root" and password "correct-password"
    Then the response status is 200
    And the login page shows the message "Invalid username or password."
    And no UI session cookie is issued

  Scenario: Correct credentials issue a session cookie and unlock the UI
    When I submit the login form with username "admin" and password "correct-password"
    Then a UI session cookie is issued
    When I open the home page
    Then the UI home page loads

  Scenario: A signed-in session can call the API
    When I submit the login form with username "admin" and password "correct-password"
    And I GET "/api/yarp/config"
    Then the response status is 200

  Scenario: A local return URL is honored after sign-in
    When I submit the login form with username "admin" and password "correct-password" and return url "/logs"
    Then the response redirects to "/logs"

  Scenario: An external return URL is ignored
    When I submit the login form with username "admin" and password "correct-password" and return url "https://evil.example.net/stolen"
    Then the response stays inside the site

  Scenario: Logout ends the session
    When I submit the login form with username "admin" and password "correct-password"
    Then a UI session cookie is issued
    When I log out
    Then the response redirects to "/login"
    When I open the home page
    Then the response redirects to the login page

  Scenario: Sign-in is refused when credentials are not configured on the server
    Given a running standalone YARP UI app configured with
      """
      {
        "YarpUi": {
          "DataDirectory": "__DATA_DIR__"
        },
        "ReverseProxy": {
          "Routes": {
            "api": { "ClusterId": "apiCluster", "Match": { "Path": "/api/{**catch-all}" } }
          },
          "Clusters": {
            "apiCluster": { "Destinations": { "primary": { "Address": "https://api.example.com" } } }
          }
        }
      }
      """
    When I submit the login form with username "admin" and password "correct-password"
    Then the response status is 200
    And the login page shows the message "Sign-in is not configured on the server. Set YarpUi:Auth in appsettings.json."
    And no UI session cookie is issued
