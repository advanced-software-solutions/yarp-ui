Feature: Proxy configuration in attach mode
  In attach mode the host app owns the proxy: YARP UI shows its whole live configuration,
  edits are written back into the very appsettings.json files the routes and clusters came
  from (hot-reloaded by YARP), the first modification takes a .yarpui.bak backup and Reset
  restores it. Items that do not come from a file (e.g. a database-backed provider) stay
  read-only and must never be shadowed or broken by a save.

  Background:
    Given an attach-mode proxy configuration service with this appsettings.json
    """
    {
      "Logging": {
        "LogLevel": { "Default": "Information" }
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
    And the live configuration is read

  Scenario: File-backed items are editable while custom-provider items are read-only
    Given the live proxy also serves route "dbRoute" pointing at cluster "dbCluster" from a custom provider
    When the live configuration is read
    Then route "api" is editable
    And cluster "apiCluster" is editable
    And route "dbRoute" is read-only

  Scenario: Saving an edited route is written back into its appsettings file
    When I apply this configuration
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
    And appsettings.json in the content root contains route "api" pointing at cluster "apiCluster"
    And appsettings.json in the content root has cluster "apiCluster" with destination "primary" at address "https://new-api.example.com"
    And unrelated settings in appsettings.json are preserved

  Scenario: A new route is written into the base appsettings file
    When I apply this configuration
      """
      {
        "Routes": [
          { "RouteId": "api", "ClusterId": "apiCluster", "Match": { "Path": "/api/{**catch-all}" } },
          { "RouteId": "extra", "ClusterId": "apiCluster", "Match": { "Path": "/extra/{**catch-all}" } }
        ],
        "Clusters": [
          { "ClusterId": "apiCluster", "Destinations": { "primary": { "Address": "https://api.example.com" } } }
        ]
      }
      """
    Then the apply succeeds
    And appsettings.json in the content root contains route "extra" pointing at cluster "apiCluster"

  Scenario: Removing a route deletes it from the appsettings file
    When I apply this configuration
      """
      {
        "Routes": [],
        "Clusters": [
          { "ClusterId": "apiCluster", "Destinations": { "primary": { "Address": "https://api.example.com" } } }
        ]
      }
      """
    Then the apply succeeds
    And appsettings.json in the content root does not contain route "api"

  Scenario: The first modification takes a backup, reset restores it
    When I apply this configuration
      """
      {
        "Routes": [
          { "RouteId": "api", "ClusterId": "apiCluster", "Match": { "Path": "/api/v3/{**catch-all}" } }
        ],
        "Clusters": [
          { "ClusterId": "apiCluster", "Destinations": { "primary": { "Address": "https://new-api.example.com" } } }
        ]
      }
      """
    Then the apply succeeds
    And an appsettings.json.yarpui.bak backup exists
    When I reset the configuration
    Then the reset succeeds
    And appsettings.json is restored to its original content

  Scenario: A route from a non-file source cannot be managed
    Given the live proxy also serves route "dbRoute" pointing at cluster "dbCluster" from a custom provider
    When the live configuration is read
    And I apply this configuration
      """
      {
        "Routes": [
          { "RouteId": "api", "ClusterId": "apiCluster", "Match": { "Path": "/api/{**catch-all}" } },
          { "RouteId": "dbRoute", "ClusterId": "dbCluster", "Match": { "Path": "/db/{**catch-all}" } }
        ],
        "Clusters": [
          { "ClusterId": "apiCluster", "Destinations": { "primary": { "Address": "https://api.example.com" } } },
          { "ClusterId": "dbCluster", "Destinations": { "only": { "Address": "https://db.example.com" } } }
        ]
      }
      """
    Then the apply fails with an error containing "comes from a non-file configuration source"

  Scenario: Deleting a cluster still used by a non-file route is rejected
    Given the live proxy also serves route "dbRoute" pointing at cluster "apiCluster" from a custom provider
    When the live configuration is read
    And I apply this configuration
      """
      {
        "Routes": [
          { "RouteId": "api", "ClusterId": "apiCluster", "Match": { "Path": "/api/{**catch-all}" } }
        ],
        "Clusters": []
      }
      """
    Then the apply fails with an error containing "is still used by route 'dbRoute'"

  Scenario: Legacy overlay items migrate into appsettings on save
    Given a legacy yarp-ui.routes.json overlay with this content
      """
      {
        "Routes": {
          "legacy": { "ClusterId": "legacyCluster", "Match": { "Path": "/legacy/{**catch-all}" } }
        },
        "Clusters": {
          "legacyCluster": { "Destinations": { "primary": { "Address": "https://legacy.example.com" } } }
        }
      }
      """
    When the live configuration is read
    And I apply this configuration
      """
      {
        "Routes": [
          { "RouteId": "api", "ClusterId": "apiCluster", "Match": { "Path": "/api/{**catch-all}" } },
          { "RouteId": "legacy", "ClusterId": "legacyCluster", "Match": { "Path": "/legacy/{**catch-all}" } }
        ],
        "Clusters": [
          { "ClusterId": "apiCluster", "Destinations": { "primary": { "Address": "https://api.example.com" } } },
          { "ClusterId": "legacyCluster", "Destinations": { "primary": { "Address": "https://legacy.example.com" } } }
        ]
      }
      """
    Then the apply succeeds
    And appsettings.json in the content root contains route "legacy" pointing at cluster "legacyCluster"
    And the overlay no longer defines route "legacy"

  Scenario: Legacy overlay items are editable
    Given a legacy yarp-ui.routes.json overlay with this content
      """
      {
        "Routes": {
          "legacy": { "ClusterId": "legacyCluster", "Match": { "Path": "/legacy/{**catch-all}" } }
        },
        "Clusters": {
          "legacyCluster": { "Destinations": { "primary": { "Address": "https://legacy.example.com" } } }
        }
      }
      """
    When the live configuration is read
    Then route "legacy" is editable
