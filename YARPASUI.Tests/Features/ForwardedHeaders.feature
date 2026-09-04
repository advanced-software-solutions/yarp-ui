Feature: Forwarded headers
  When the whole app sits behind a trusted front (a Cloudflare tunnel, nginx, another load
  balancer) the direct connection address is the front's, not the visitor's. The opt-in
  YarpUi:ForwardedHeaders section resolves Connection.RemoteIpAddress from a trusted header
  so IP blocking and the request log see the real client — in every hosting mode, without
  host code, and without the spoofable X-Forwarded-For chain.

  Background:
    Given a running standalone YARP UI app with forwarded headers
    And I am signed in to the UI

  Scenario: The configured header resolves the client IP over the forwarded-for chain
    When I request "/proxied/one" with CF-Connecting-IP "203.0.113.9" and X-Forwarded-For "198.51.100.99"
    Then the response status is 502
    And the last log entry has status 502 and client IP "203.0.113.9"

  Scenario: IP blocking matches the resolved address without the forwarded-for toggle
    When I add the IP blocking rule "203.0.113.77"
    Then the response status is 200
    When I request "/proxied/two" with CF-Connecting-IP "203.0.113.77"
    Then the response status is 403
    And the last log entry has status 403 and client IP "203.0.113.77"

  Scenario: Requests from other resolved addresses pass through
    When I add the IP blocking rule "203.0.113.77"
    Then the response status is 200
    When I request "/proxied/three" with CF-Connecting-IP "198.51.100.1"
    Then the response status is 502
    And the last log entry has status 502 and client IP "198.51.100.1"

  Scenario: Without the section the custom header is ignored
    Given a running standalone YARP UI app without forwarded headers
    And I am signed in to the UI
    When I request "/proxied/four" with CF-Connecting-IP "203.0.113.9" and X-Forwarded-For "198.51.100.99"
    Then the response status is 502
    And the last log entry has status 502 and client IP "198.51.100.99"
