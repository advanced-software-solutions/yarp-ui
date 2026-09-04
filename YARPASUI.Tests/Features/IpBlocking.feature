Feature: IP blocking
  Client addresses on the block list are rejected with 403 before they reach anything else —
  routing, the proxy or the host's own endpoints. Rules cover single addresses, CIDR
  networks and from–to ranges in IPv4 and IPv6, are persisted to
  yarp-ui-ipblocklist.json in the data directory, and the management UI itself is never
  blocked. Blocked hits are captured into the request log so they are visible on the
  Logs page.

  Background:
    Given a running standalone YARP UI app configured with
      """
      {
        "YarpUi": {
          "DataDirectory": "__DATA_DIR__",
          "Auth": { "Username": "admin", "Password": "correct-password" }
        }
      }
      """
    And I am signed in to the UI

  Scenario: The block list starts empty
    When I GET "/api/yarp/ipblocking"
    Then the response status is 200
    And the response json ip blocking rules count is 0
    And the response json trust forwarded for is "false"

  Scenario: Rules can be added in every notation
    When I add the IP blocking rule "203.0.113.7"
    And I add the IP blocking rule "203.0.113.0/24"
    And I add the IP blocking rule "203.0.113.200-203.0.114.10"
    And I add the IP blocking rule "2001:db8::/32"
    Then the response status is 200
    When I GET "/api/yarp/ipblocking"
    Then the response status is 200
    And the ip blocking rules are
      | Value                      | Kind   |
      | 203.0.113.7                | single |
      | 203.0.113.0/24             | cidr   |
      | 203.0.113.200-203.0.114.10 | range  |
      | 2001:db8::/32              | cidr   |
    And the block list file exists

  Scenario: Rule values are canonicalized
    When I add the IP blocking rule "::ffff:198.51.100.4"
    And I add the IP blocking rule "2001:DB8::/32"
    When I GET "/api/yarp/ipblocking"
    Then the response status is 200
    And the ip blocking rules are
      | Value         | Kind   |
      | 198.51.100.4  | single |
      | 2001:db8::/32 | cidr   |

  Scenario: Duplicate rules are rejected
    When I add the IP blocking rule "203.0.113.7"
    And I add the IP blocking rule "203.0.113.7"
    Then the response status is 400
    And the response json errors include "already blocked"
    When I GET "/api/yarp/ipblocking"
    Then the response json ip blocking rules count is 1

  Scenario Outline: Invalid rule values are rejected
    When I add the IP blocking rule "<Value>"
    Then the response status is 400
    And the response json errors include "<Error>"

    Examples:
      | Value                | Error        |
      | banana               | not a valid  |
      | 203.0.113.5/24       | host bits    |
      | 10.0.0.9-10.0.0.1    | out of order |
      | 10.0.0.1-2001:db8::1 | mixes        |
      |                      | Enter an IP  |

  Scenario: Rules can be removed
    When I add the IP blocking rule "203.0.113.7"
    And I remove the IP blocking rule "203.0.113.7"
    Then the response status is 204
    When I GET "/api/yarp/ipblocking"
    Then the response json ip blocking rules count is 0
    When I remove an unknown IP blocking rule
    Then the response status is 404

  Scenario: The forwarded-for setting round-trips and persists
    When I set the IP blocking forwarded-for setting to "true"
    Then the response status is 200
    When I GET "/api/yarp/ipblocking"
    Then the response json trust forwarded for is "true"
    And the persisted block list file says trust forwarded for is "true"

  Scenario: The check endpoint reports the matching rule
    When I add the IP blocking rule "203.0.113.0/24"
    And I check the address "203.0.113.55"
    Then the response status is 200
    And the response json says the address is blocked by "203.0.113.0/24"
    When I check the address "198.51.100.1"
    Then the response status is 200
    And the response json says the address is not blocked

  Scenario: Blocked requests are rejected and logged
    Given the IP blocking rule "203.0.113.77" and forwarded-for trust are configured
    When I request "/some/proxied/path" with X-Forwarded-For "203.0.113.77"
    Then the response status is 403
    And the blocked request was logged with status 403 and client IP "203.0.113.77"

  Scenario: Requests from other addresses pass through
    Given the IP blocking rule "203.0.113.77" and forwarded-for trust are configured
    When I request "/some/proxied/path" with X-Forwarded-For "198.51.100.1"
    Then the response status is 404

  Scenario: The management UI is never blocked
    Given the IP blocking rule "203.0.113.77" and forwarded-for trust are configured
    When I request "/api/yarp/ipblocking" with X-Forwarded-For "203.0.113.77"
    Then the response status is 200
    When I request "/login" with X-Forwarded-For "203.0.113.77"
    Then the response status is 200

  Scenario: Rules survive a restart
    Given a running standalone YARP UI app with a retained data directory
    And I am signed in to the UI
    When I add the IP blocking rule "203.0.113.0/24"
    And the app is restarted
    Given I am signed in to the UI
    When I GET "/api/yarp/ipblocking"
    Then the response status is 200
    And the ip blocking rules are
      | Value          | Kind   |
      | 203.0.113.0/24 | cidr   |
