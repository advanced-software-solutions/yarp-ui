Feature: Proxy configuration in standalone mode
  YARP UI owns the proxy configuration in standalone mode: it starts from the appsettings.json
  seed (yarp-ui.routes.json overrides it once the UI has saved changes), validates every edit
  before applying it, persists accepted changes to disk and can reset back to the seed.
  These scenarios protect that lifecycle from regressions.

  Background:
    Given a standalone proxy configuration service
    And an appsettings.json with this ReverseProxy section
    """
    {
      "Routes": {
        "api": { "ClusterId": "apiCluster", "Match": { "Path": "/api/{**catch-all}" } },
        "web": { "ClusterId": "webCluster", "Order": 100, "Match": { "Path": "/{**catch-all}" } }
      },
      "Clusters": {
        "apiCluster": { "Destinations": { "primary": { "Address": "https://api.example.com" } } },
        "webCluster": { "Destinations": { "primary": { "Address": "https://web.example.com" } } }
      }
    }
    """

  Scenario: Initial configuration comes from the appsettings seed
    When the initial configuration is loaded
    Then the configuration contains routes
      | RouteId | ClusterId  |
      | api     | apiCluster |
      | web     | webCluster |
    And the configuration contains clusters
      | ClusterId  | Destinations |
      | apiCluster | primary      |
      | webCluster | primary      |
    And the UI does not manage the configuration yet

  Scenario: Environment-specific appsettings override the base seed
    Given an appsettings.Testing.json that changes the seed like this
      """
      {
        "Clusters": {
          "apiCluster": { "Destinations": { "primary": { "Address": "https://staging.example.com" } } }
        }
      }
      """
    When the initial configuration is loaded
    Then cluster "apiCluster" has destination "primary" at address "https://staging.example.com"

  Scenario: Appsettings in the data directory override the content-root ones
    Given a data-directory appsettings.json with this ReverseProxy section
      """
      {
        "Clusters": {
          "apiCluster": { "Destinations": { "primary": { "Address": "https://volume.example.com" } } }
        }
      }
      """
    When the initial configuration is loaded
    Then cluster "apiCluster" has destination "primary" at address "https://volume.example.com"

  Scenario: A saved UI overlay takes precedence over the seed
    Given a yarp-ui.routes.json overlay with this content
      """
      {
        "Routes": {
          "onlyUi": { "ClusterId": "uiCluster", "Match": { "Path": "/ui/{**catch-all}" } }
        },
        "Clusters": {
          "uiCluster": { "Destinations": { "primary": { "Address": "https://ui.example.com" } } }
        }
      }
      """
    When the initial configuration is loaded
    Then the configuration contains routes
      | RouteId | ClusterId |
      | onlyUi  | uiCluster |
    And the configuration does not contain route "api"
    And the UI manages the configuration

  Scenario: An empty overlay falls back to the seed
    Given a yarp-ui.routes.json overlay with this content
      """
      {
        "Routes": {},
        "Clusters": {}
      }
      """
    When the initial configuration is loaded
    Then the configuration contains routes
      | RouteId | ClusterId  |
      | api     | apiCluster |
      | web     | webCluster |

  Scenario: A corrupt overlay falls back to the seed instead of breaking the proxy
    Given a corrupt yarp-ui.routes.json file
    When the initial configuration is loaded
    Then the configuration contains routes
      | RouteId | ClusterId  |
      | api     | apiCluster |
      | web     | webCluster |

  Scenario: Gateway-only entries parked under Clusters are hidden from the live view
    Given an appsettings.json with this ReverseProxy section
      """
      {
        "Routes": {
          "api": { "ClusterId": "apiCluster", "Match": { "Path": "/api/{**catch-all}" } }
        },
        "Clusters": {
          "apiCluster": { "Destinations": { "primary": { "Address": "https://api.example.com" } } },
          "gatewaySettings": { "HttpRequest": { "ActivityTimeout": "00:02:00" } }
        }
      }
      """
    When the initial configuration is loaded
    And the live configuration is read
    Then the configuration contains clusters
      | ClusterId  | Destinations |
      | apiCluster | primary      |
    And the configuration does not contain cluster "gatewaySettings"
    And every route and cluster is editable

  Scenario: Applying a valid configuration updates the live view and persists the overlay
    When the initial configuration is loaded
    And I apply this configuration
      """
      {
        "Routes": [
          { "RouteId": "api", "ClusterId": "apiCluster", "Match": { "Path": "/api/{**catch-all}" } }
        ],
        "Clusters": [
          { "ClusterId": "apiCluster", "Destinations": { "primary": { "Address": "https://new-api.example.com" } } }
        ]
      }
      """
    Then the apply succeeds
    And the configuration contains routes
      | RouteId | ClusterId  |
      | api     | apiCluster |
    And the configuration does not contain route "web"
    And cluster "apiCluster" has destination "primary" at address "https://new-api.example.com"
    And the yarp-ui.routes.json file exists
    And the yarp-ui.routes.json file contains routes
      | RouteId |
      | api     |
    And every route and cluster is editable

  Scenario: A persisted overlay is the source on the next startup
    When the initial configuration is loaded
    And I apply this configuration
      """
      {
        "Routes": [
          { "RouteId": "api", "ClusterId": "apiCluster", "Match": { "Path": "/api/v2/{**catch-all}" } }
        ],
        "Clusters": [
          { "ClusterId": "apiCluster", "Destinations": { "primary": { "Address": "https://new-api.example.com" } } }
        ]
      }
      """
    Then the apply succeeds
    When the initial configuration is loaded
    Then the configuration contains routes
      | RouteId | ClusterId  |
      | api     | apiCluster |
    And the configuration does not contain route "web"

  Scenario: Resetting returns to the seed and removes the overlay
    When the initial configuration is loaded
    And I apply this configuration
      """
      {
        "Routes": [
          { "RouteId": "replaced", "ClusterId": "apiCluster", "Match": { "Path": "/x/{**catch-all}" } }
        ],
        "Clusters": [
          { "ClusterId": "apiCluster", "Destinations": { "primary": { "Address": "https://new-api.example.com" } } }
        ]
      }
      """
    Then the apply succeeds
    When I reset the configuration
    Then the reset succeeds
    And the configuration contains routes
      | RouteId | ClusterId  |
      | api     | apiCluster |
      | web     | webCluster |
    And cluster "apiCluster" has destination "primary" at address "https://api.example.com"
    And the yarp-ui.routes.json file does not exist

  Scenario: Duplicate route ids are rejected
    When the initial configuration is loaded
    And I apply this configuration
      """
      {
        "Routes": [
          { "RouteId": "dupe", "ClusterId": "apiCluster", "Match": { "Path": "/a/{**catch-all}" } },
          { "RouteId": "dupe", "ClusterId": "apiCluster", "Match": { "Path": "/b/{**catch-all}" } }
        ],
        "Clusters": []
      }
      """
    Then the apply fails with an error containing "Duplicate route id 'dupe'"
    And the yarp-ui.routes.json file does not exist

  Scenario: Duplicate cluster ids are rejected
    When the initial configuration is loaded
    And I apply this configuration
      """
      {
        "Routes": [],
        "Clusters": [
          { "ClusterId": "dupe", "Destinations": { "a": { "Address": "https://a.example.com" } } },
          { "ClusterId": "dupe", "Destinations": { "b": { "Address": "https://b.example.com" } } }
        ]
      }
      """
    Then the apply fails with an error containing "Duplicate cluster id 'dupe'"

  Scenario: A route without a cluster is rejected
    When the initial configuration is loaded
    And I apply this configuration
      """
      {
        "Routes": [
          { "RouteId": "lost", "ClusterId": null, "Match": { "Path": "/lost/{**catch-all}" } }
        ],
        "Clusters": []
      }
      """
    Then the apply fails with an error containing "Route 'lost' has no cluster assigned"

  Scenario: A route referencing an unknown cluster is rejected
    When the initial configuration is loaded
    And I apply this configuration
      """
      {
        "Routes": [
          { "RouteId": "api", "ClusterId": "nowhere", "Match": { "Path": "/api/{**catch-all}" } }
        ],
        "Clusters": []
      }
      """
    Then the apply fails with an error containing "Route 'api' references unknown cluster 'nowhere'"

  Scenario: A pre-existing dangling cluster reference is tolerated
    When the initial configuration is loaded
    Given the live proxy already runs route "legacy" pointing at the missing cluster "gone"
    And I apply this configuration
      """
      {
        "Routes": [
          { "RouteId": "legacy", "ClusterId": "gone", "Match": { "Path": "/legacy/{**catch-all}" } }
        ],
        "Clusters": []
      }
      """
    Then the apply succeeds

  Scenario: Duplicate destinations inside one cluster are rejected
    When the initial configuration is loaded
    And I apply this configuration
      """
      {
        "Routes": [],
        "Clusters": [
          {
            "ClusterId": "apiCluster",
            "Destinations": {
              "primary": { "Address": "https://a.example.com" },
              "PRIMARY": { "Address": "https://b.example.com" }
            }
          }
        ]
      }
      """
    Then the apply fails with an error containing "duplicate destination"

  Scenario: Validator failures are surfaced as apply errors
    Given the proxy validator rejects route "api" with "no path template is set"
    When the initial configuration is loaded
    And I apply this configuration
      """
      {
        "Routes": [
          { "RouteId": "api", "ClusterId": "apiCluster", "Match": { "Path": "/api/{**catch-all}" } }
        ],
        "Clusters": []
      }
      """
    Then the apply fails with an error containing "Route 'api': no path template is set"

  Scenario: The data directory defaults to the content root
    Given the configuration setting YarpUi:DataDirectory is "   "
    When the data directory is resolved
    Then the resolved data directory is the content root

  Scenario: A relative data directory resolves under the content root
    Given the configuration setting YarpUi:DataDirectory is "proxy-data"
    When the data directory is resolved
    Then the resolved data directory is "proxy-data" under the content root

  Scenario: An absolute data directory is used as-is
    Given the configuration setting YarpUi:DataDirectory is "C:/yarp-ui-volume"
    When the data directory is resolved
    Then the resolved data directory is the configured absolute path
