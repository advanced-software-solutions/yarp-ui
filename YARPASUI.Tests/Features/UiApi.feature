Feature: Management API
  The /api/yarp endpoints serve the editor and logs pages. They are protected by the UI's
  authorization policy, validate their inputs and reflect exactly what the services do —
  these scenarios pin both the payloads and the behavior of every endpoint.

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
    And I am signed in to the UI

  Scenario: The current configuration is served
    When I GET "/api/yarp/config"
    Then the response status is 200
    And the response json routes are
      | RouteId | ClusterId  |
      | api     | apiCluster |
    And the response json editable route ids are
      | RouteId |
      | api     |
    And the response json attach mode is "false"
    And the response json managed by UI flag is "false"

  Scenario: A valid update is applied and persisted
    When I PUT "/api/yarp/config" with json
      """
      {
        "Routes": [
          { "RouteId": "api", "ClusterId": "apiCluster", "Match": { "Path": "/api/{**catch-all}" } },
          { "RouteId": "admin", "ClusterId": "adminCluster", "Match": { "Path": "/admin/{**catch-all}" } }
        ],
        "Clusters": [
          { "ClusterId": "apiCluster", "Destinations": { "primary": { "Address": "https://v2.api.example.com" } } },
          { "ClusterId": "adminCluster", "Destinations": { "only": { "Address": "https://admin.example.com" } } }
        ]
      }
      """
    Then the response status is 200
    And the response json routes are
      | RouteId | ClusterId   |
      | api     | apiCluster  |
      | admin   | adminCluster |
    And the response json managed by UI flag is "true"
    And the persisted UI config file contains routes
      | RouteId |
      | api     |
      | admin   |

  Scenario: An update with invalid JSON is rejected
    When I PUT "/api/yarp/config" with invalid json
    Then the response status is 400
    And the response json errors include "not valid JSON"

  Scenario: An update with a null body is rejected
    When I PUT "/api/yarp/config" with a null json body
    Then the response status is 400
    And the response json errors include "empty"

  Scenario: A configuration that fails validation is rejected
    When I PUT "/api/yarp/config" with json
      """
      {
        "Routes": [
          { "RouteId": "broken", "ClusterId": "apiCluster", "Match": {} }
        ],
        "Clusters": [
          { "ClusterId": "apiCluster", "Destinations": { "primary": { "Address": "https://api.example.com" } } }
        ]
      }
      """
    Then the response status is 400
    And the response json errors include "Route 'broken'"
    And the persisted UI config file does not exist

  Scenario: Reset returns the seed configuration and removes the persisted file
    When I PUT "/api/yarp/config" with json
      """
      {
        "Routes": [
          { "RouteId": "replaced", "ClusterId": "apiCluster", "Match": { "Path": "/x/{**catch-all}" } }
        ],
        "Clusters": [
          { "ClusterId": "apiCluster", "Destinations": { "primary": { "Address": "https://api.example.com" } } }
        ]
      }
      """
    Then the response status is 200
    When I POST "/api/yarp/config/reset"
    Then the response status is 200
    And the response json routes are
      | RouteId | ClusterId  |
      | api     | apiCluster |
    And the persisted UI config file does not exist

  Scenario: Logged requests are listed and can be polled incrementally
    Given the proxy has logged these requests
      | Method | Path   | Status | DurationMs | RouteId |
      | GET    | /one   | 200    | 10         | api     |
      | GET    | /two   | 500    | 20         | api     |
    When I GET "/api/yarp/logs"
    Then the response status is 200
    And the response json entries are
      | Method | Path | Status |
      | GET    | /one | 200    |
      | GET    | /two | 500    |

  Scenario: Logged requests can be cleared
    Given the proxy has logged these requests
      | Method | Path | Status | DurationMs | RouteId |
      | GET    | /one | 200    | 10         | api     |
    When I DELETE "/api/yarp/logs"
    Then the response status is 204
    When I GET "/api/yarp/logs"
    Then the response json entries count is 0

  Scenario: The stats endpoint aggregates the requested time window
    Given the proxy has logged these requests
      | Method | Path   | Status | DurationMs | RouteId |
      | GET    | /a     | 200    | 10         | api     |
      | GET    | /b     | 500    | 20         | api     |
      | GET    | /c     | 200    | 30         | api     |
    When I GET "/api/yarp/logs/stats?minutes=60"
    Then the response status is 200
    And the response json stats count is 3

  Scenario: The retention settings round-trip
    When I GET "/api/yarp/logs/settings"
    Then the response status is 200
    And the response json retention days is 30
    When I PUT "/api/yarp/logs/settings" with json
      """
      { "retentionDays": 7 }
      """
    Then the response status is 200
    And the response json retention days is 7
    When I GET "/api/yarp/logs/settings"
    Then the response json retention days is 7

  Scenario: Retention zero (keep forever) is accepted
    When I PUT "/api/yarp/logs/settings" with json
      """
      { "retentionDays": 0 }
      """
    Then the response status is 200
    And the response json retention days is 0

  Scenario Outline: Invalid retention values are rejected
    When I PUT "/api/yarp/logs/settings" with json
      """
      <body>
      """
    Then the response status is 400

    Examples:
      | case       | body                        |
      | negative   | { "retentionDays": -1 }     |
      | too large  | { "retentionDays": 4000 }   |
      | null value | { "retentionDays": null }   |
      | missing    | {}                          |

  Scenario: Changing the retention policy from the API purges old entries immediately
    Given an entry from 10 days ago was written directly to the log database
    When I PUT "/api/yarp/logs/settings" with json
      """
      { "retentionDays": 1 }
      """
    Then the response status is 200
    When I GET "/api/yarp/logs"
    Then the response json entries count is 0

  Scenario: Logged entries include the captured client IP
    Given the proxy has logged these requests
      | Method | Path | Status | DurationMs | RouteId | ClientIp    |
      | GET    | /one | 200    | 10         | api     | 203.0.113.9 |
    When I GET "/api/yarp/logs"
    Then the response status is 200
    And the response json entries are
      | Method | Path | Status | ClientIp    |
      | GET    | /one | 200    | 203.0.113.9 |

  Scenario: Searched logs come back newest first with a total match count
    Given the proxy has logged these requests
      | Method | Path    | Status | DurationMs | RouteId |
      | GET    | /first  | 200    | 10         | api     |
      | GET    | /second | 200    | 10         | api     |
    When I GET "/api/yarp/logs?limit=10"
    Then the response status is 200
    And the response json entries are
      | Path    |
      | /second |
      | /first  |
    And the response json total is 2

  Scenario: Logs can be filtered by route, cluster and destination
    Given the proxy has logged these requests
      | Method | Path | Status | DurationMs | RouteId | ClusterId | DestinationId |
      | GET    | /a   | 200    | 10         | api     | c1        | d1            |
      | GET    | /b   | 200    | 10         | web     | c2        | d2            |
      | GET    | /c   | 200    | 10         | api     | c2        | d3            |
    When I GET "/api/yarp/logs?routeId=api"
    Then the response status is 200
    And the response json entries are
      | Path |
      | /c   |
      | /a   |
    When I GET "/api/yarp/logs?clusterId=c1"
    Then the response status is 200
    And the response json entries count is 1
    When I GET "/api/yarp/logs?destinationId=d2"
    Then the response status is 200
    And the response json entries are
      | Path |
      | /b   |

  Scenario: Logs can be restricted to a time frame
    Given the proxy has logged these requests
      | Method | Path   | Status | DurationMs | RouteId |
      | GET    | /fresh | 200    | 10         | api     |
    And an entry from 10 days ago was written directly to the log database
    When I GET "/api/yarp/logs" with a time range covering the last 5 days
    Then the response status is 200
    And the response json entries are
      | Path   |
      | /fresh |
    And the response json total is 1

  Scenario: Logs can be sorted by duration
    Given the proxy has logged these requests
      | Method | Path | Status | DurationMs | RouteId |
      | GET    | /a   | 200    | 10         | api     |
      | GET    | /b   | 200    | 30         | api     |
      | GET    | /c   | 200    | 20         | api     |
    When I GET "/api/yarp/logs?sort=duration&desc=false"
    Then the response status is 200
    And the response json entries are
      | Path |
      | /a   |
      | /c   |
      | /b   |

  Scenario Outline: Invalid log query parameters are rejected
    When I GET "<url>"
    Then the response status is 400

    Examples:
      | case       | url                          |
      | bad sort   | /api/yarp/logs?sort=bogus    |
      | zero limit | /api/yarp/logs?limit=0       |
      | big limit  | /api/yarp/logs?limit=5000    |
