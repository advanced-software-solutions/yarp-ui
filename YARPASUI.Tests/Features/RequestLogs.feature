Feature: Request log persistence and performance stats
  Proxied requests are captured into a SQLite store (yarp-ui-logs.db) so they survive
  restarts, can be polled incrementally by the Logs page, feed its performance panel
  (summary, percentiles, per-route aggregates, buckets) and honor a retention policy.

  Background:
    Given a request log store with default retention 30 days

  Scenario: Captured requests are persisted and returned oldest first
    Given these proxied requests were captured
      | Method | Path    | Status | DurationMs | RouteId |
      | GET    | /api/a  | 200    | 12.5       | api     |
      | POST   | /api/b  | 500    | 40         | api     |
      | GET    | /web/c  | -      | 7          | -       |
    When the pending entries are flushed
    And all entries are read
    Then the returned entries are
      | Method | Path    | Status | DurationMs | RouteId |
      | GET    | /api/a  | 200    | 12.5       | api     |
      | POST   | /api/b  | 500    | 40         | api     |
      | GET    | /web/c  | -      | 7          | -       |
    And the entries are ordered by ascending sequence numbers

  Scenario: Polling returns only entries after the last seen sequence
    Given these proxied requests were captured
      | Method | Path   | Status | DurationMs | RouteId |
      | GET    | /one   | 200    | 10         | api     |
      | GET    | /two   | 200    | 10         | api     |
      | GET    | /three | 200    | 10         | api     |
    When the pending entries are flushed
    And all entries are read
    And the entries are read after sequence 1
    Then the returned entries count is 2
    And the returned entries are
      | Method | Path   | Status | DurationMs | RouteId |
      | GET    | /two   | 200    | 10         | api     |
      | GET    | /three | 200    | 10         | api     |

  Scenario: Log entries survive a restart
    Given these proxied requests were captured
      | Method | Path   | Status | DurationMs | RouteId |
      | GET    | /keep  | 200    | 10         | api     |
    When the pending entries are flushed
    And the store is reopened with default retention 90 days
    And all entries are read
    Then the returned entries count is 1

  Scenario: Clearing removes every entry
    Given these proxied requests were captured
      | Method | Path   | Status | DurationMs | RouteId |
      | GET    | /one   | 200    | 10         | api     |
      | GET    | /two   | 200    | 10         | api     |
    When the pending entries are flushed
    And the log is cleared
    And all entries are read
    Then the returned entries count is 0

  Scenario: Stats over an empty store are zero
    When stats are computed over all time
    Then the stats summary shows
      | Count | Errors | AvgMs | MaxMs |
      | 0     | 0      | 0     | 0     |

  Scenario: The summary aggregates counts, errors and durations
    Given these proxied requests were captured
      | Method | Path    | Status | DurationMs | RouteId |
      | GET    | /api/a  | 200    | 10         | api     |
      | POST   | /api/b  | 500    | 20         | api     |
      | GET    | /api/c  | -      | 40         | api     |
    When the pending entries are flushed
    And stats are computed over all time
    Then the stats summary shows
      | Count | Errors | AvgMs | MaxMs |
      | 3     | 2      | 23.33 | 40    |

  Scenario: Percentiles use nearest-rank over the sorted durations
    Given these proxied requests were captured
      | Method | Path   | Status | DurationMs | RouteId |
      | GET    | /req1  | 200    | 1          | api     |
      | GET    | /req2  | 200    | 2          | api     |
      | GET    | /req3  | 200    | 3          | api     |
      | GET    | /req4  | 200    | 4          | api     |
      | GET    | /req5  | 200    | 5          | api     |
      | GET    | /req6  | 200    | 6          | api     |
      | GET    | /req7  | 200    | 7          | api     |
      | GET    | /req8  | 200    | 8          | api     |
      | GET    | /req9  | 200    | 9          | api     |
      | GET    | /req10 | 200    | 10         | api     |
    When the pending entries are flushed
    And stats are computed over all time
    Then the stats percentiles are P50 5 ms, P95 10 ms and P99 10 ms

  Scenario: Per-route aggregates are ordered by worst P95 first
    Given these proxied requests were captured
      | Method | Path       | Status | DurationMs | RouteId |
      | GET    | /fast/1    | 200    | 10         | fast    |
      | GET    | /fast/2    | 200    | 20         | fast    |
      | GET    | /fast/3    | 200    | 30         | fast    |
      | GET    | /slow/1    | 200    | 50         | slow    |
      | GET    | /slow/2    | 500    | 60         | slow    |
      | GET    | /slow/3    | 200    | 70         | slow    |
    When the pending entries are flushed
    And stats are computed over all time
    Then the stats route aggregates are
      | RouteId | Count | Errors |
      | slow    | 3     | 1      |
      | fast    | 3     | 0      |

  Scenario: The time window excludes older entries
    Given these proxied requests were captured
      | Method | Path     | Status | DurationMs | RouteId |
      | GET    | /now/1   | 200    | 10         | api     |
      | GET    | /now/2   | 200    | 20         | api     |
      | GET    | /now/3   | 200    | 30         | api     |
    And 2 entries from 60 days ago were written directly to the database
    When the pending entries are flushed
    And stats are computed over the last 5 minutes
    Then the stats summary shows
      | Count | Errors | AvgMs | MaxMs |
      | 3     | 0      | 20    | 30    |

  Scenario: The retention policy defaults to the configured value
    Then the retention policy is 30 days

  Scenario: Changing the retention policy round-trips
    When the retention policy is set to 7 days
    Then the retention policy is 7 days

  Scenario: Retention zero keeps logs forever
    Given 2 entries from 100 days ago were written directly to the database
    When the retention policy is set to 0 days
    And retention is applied
    Then the database contains 2 entries

  Scenario: Retention purges entries older than the policy
    Given 2 entries from 100 days ago were written directly to the database
    And these proxied requests were captured
      | Method | Path   | Status | DurationMs | RouteId |
      | GET    | /fresh | 200    | 10         | api     |
    When the pending entries are flushed
    And the retention policy is set to 50 days
    And retention is applied
    Then the database contains 1 entries

  Scenario: A saved retention policy survives restarts and overrides the config default
    When the retention policy is set to 7 days
    And the store is reopened with default retention 90 days
    Then the retention policy is 7 days
