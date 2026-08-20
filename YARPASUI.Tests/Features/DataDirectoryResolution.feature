@windows
Feature: Data directory resolution
  Mutable state (the request log database, the UI-managed routes file) lives in the data
  directory, which defaults to the content root. Under IIS the default application pool
  identity cannot write to the site folder, so a read-only content root must fall back to
  a writable directory at startup instead of crashing with SQLite error 14.

  Scenario: A read-only content root falls back to a writable data directory
    Given the content root is not writable
    And the fallback data directory is a temp folder
    When the app starts
    Then the fallback data directory is the effective YarpUi:DataDirectory
    And the request log database is created in the fallback data directory
    And the content root holds no request log database

  Scenario: An explicitly configured data directory is honored even when the content root is read-only
    Given the content root is not writable
    And YarpUi:DataDirectory points at a temp folder
    When the app starts
    Then the request log database is created in that data directory
