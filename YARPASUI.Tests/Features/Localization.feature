Feature: Localization
  The UI ships in English, Arabic, Spanish and Simplified Chinese. A request's culture is
  resolved from the query string (?culture=), the standard ASP.NET Core culture cookie and
  the Accept-Language header, defaulting to English; Arabic also renders right-to-left.
  The localization middleware is scoped to the UI's own routes, so hosts never need to
  call UseRequestLocalization themselves.

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

  Scenario: The default UI language is English
    Given I am signed in to the UI
    When I open the home page
    Then the page declares culture "en" and direction "ltr"
    And the page contains "Route Map — YARP UI"

  Scenario: Arabic is selected with the culture query and renders right-to-left
    Given I am signed in to the UI
    When I GET "/?culture=ar&ui-culture=ar"
    Then the page declares culture "ar" and direction "rtl"
    And the page contains "الخريطة"

  Scenario: The login page honors the culture before sign-in
    When I GET "/login?culture=es&ui-culture=es"
    Then the page declares culture "es" and direction "ltr"
    And the page contains "Iniciar sesión"
    And the page contains "Contraseña"

  Scenario: The culture cookie persists the language across requests
    Given I am signed in to the UI
    And the UI culture cookie is set to "es"
    When I open the home page
    Then the page declares culture "es" and direction "ltr"
    And the page contains "Mapa de rutas — YARP UI"

  Scenario: zh-CN resolves to the Simplified Chinese resources
    Given I am signed in to the UI
    When I GET "/?culture=zh-CN&ui-culture=zh-CN"
    Then the page declares culture "zh-CN" and direction "ltr"
    And the page contains "路由地图"

  Scenario: Unsupported cultures fall back to the default
    Given I am signed in to the UI
    When I GET "/?culture=fr&ui-culture=fr"
    Then the page declares culture "en" and direction "ltr"
    And the page contains "Route Map — YARP UI"

  Scenario: API validation errors are localized
    Given I am signed in to the UI
    When I PUT "/api/yarp/config?culture=ar&ui-culture=ar" with invalid json
    Then the response status is 400
    And the response json errors include "ليس JSON صالحًا"

  Scenario: Login error messages are localized
    Given the UI culture cookie is set to "es"
    When I submit the login form with username "admin" and password "wrong-password"
    Then the login page shows the message "Usuario o contraseña incorrectos."
