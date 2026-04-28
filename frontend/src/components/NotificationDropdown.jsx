import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import {
  formatAuditEntry,
  formatDateTime,
  formatRelativeTime,
  getAuditLogs,
  getOrders,
  parseUtcDate,
} from "../services/api";

const POLL_INTERVAL_MS = 20000;
const STORAGE_KEY = "order-processing.notifications.lastReadAt";
const MAX_NOTIFICATIONS = 25;

function readStoredLastReadAt() {
  if (typeof window === "undefined") {
    return "";
  }

  return window.localStorage.getItem(STORAGE_KEY) ?? "";
}

function writeStoredLastReadAt(value) {
  if (typeof window === "undefined") {
    return;
  }

  window.localStorage.setItem(STORAGE_KEY, value);
}

function getEntryTimestamp(entry) {
  return entry?.createdAt ? (parseUtcDate(entry.createdAt)?.getTime() ?? 0) : 0;
}

function BellIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path
        d="M12 3a4 4 0 0 0-4 4v1.1c0 .88-.32 1.72-.89 2.39L5.4 12.4A2 2 0 0 0 6.92 16h10.16a2 2 0 0 0 1.52-3.6l-1.71-1.91A3.72 3.72 0 0 1 16 8.1V7a4 4 0 0 0-4-4Zm0 18a2.5 2.5 0 0 0 2.45-2h-4.9A2.5 2.5 0 0 0 12 21Z"
        fill="currentColor"
      />
    </svg>
  );
}

function NotificationDropdown() {
  const navigate = useNavigate();
  const [isOpen, setIsOpen] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [entries, setEntries] = useState([]);
  const [lastReadAt, setLastReadAt] = useState(() => readStoredLastReadAt());
  const containerRef = useRef(null);

  const markAllAsRead = useCallback(
    (nextEntries = entries) => {
      const latestTimestamp = nextEntries[0]?.createdAt ?? new Date().toISOString();
      setLastReadAt(latestTimestamp);
      writeStoredLastReadAt(latestTimestamp);
    },
    [entries]
  );

  useEffect(() => {
    let isDisposed = false;

    async function loadNotifications() {
      if (document.hidden) {
        return;
      }

      try {
        setError("");
        const [auditData, ordersData] = await Promise.all([getAuditLogs(), getOrders()]);
        if (isDisposed) {
          return;
        }

        const ordersById = new Map(
          (Array.isArray(ordersData) ? ordersData : []).map((order) => [order.id, order])
        );

        const nextEntries = (Array.isArray(auditData) ? auditData : [])
          .sort((left, right) => getEntryTimestamp(right) - getEntryTimestamp(left))
          .slice(0, MAX_NOTIFICATIONS)
          .map((entry) => ({
            ...entry,
            orderNumber: ordersById.get(entry.entityId)?.orderNumber ?? "",
            message: formatAuditEntry(entry, ordersById.get(entry.entityId)),
          }));

        setEntries(nextEntries);
      } catch (requestError) {
        if (!isDisposed) {
          console.error("Failed to load notifications:", requestError);
          setEntries([]);
          setError(requestError.message || "Failed to load notifications");
        }
      } finally {
        if (!isDisposed) {
          setLoading(false);
        }
      }
    }

    loadNotifications();
    const intervalId = window.setInterval(loadNotifications, POLL_INTERVAL_MS);

    function handleVisibilityChange() {
      if (!document.hidden) {
        loadNotifications();
      }
    }

    document.addEventListener("visibilitychange", handleVisibilityChange);

    return () => {
      isDisposed = true;
      window.clearInterval(intervalId);
      document.removeEventListener("visibilitychange", handleVisibilityChange);
    };
  }, []);

  useEffect(() => {
    if (!isOpen) {
      return undefined;
    }

    function handlePointerDown(event) {
      if (!containerRef.current?.contains(event.target)) {
        setIsOpen(false);
      }
    }

    function handleEscape(event) {
      if (event.key === "Escape") {
        setIsOpen(false);
      }
    }

    document.addEventListener("mousedown", handlePointerDown);
    document.addEventListener("keydown", handleEscape);

    return () => {
      document.removeEventListener("mousedown", handlePointerDown);
      document.removeEventListener("keydown", handleEscape);
    };
  }, [isOpen]);

  useEffect(() => {
    if (!isOpen || entries.length === 0) {
      return;
    }

    markAllAsRead(entries);
  }, [entries, isOpen, markAllAsRead]);

  const lastReadTime = useMemo(() => (lastReadAt ? new Date(lastReadAt).getTime() : 0), [lastReadAt]);

  const unreadCount = useMemo(() => {
    return entries.filter((entry) => getEntryTimestamp(entry) > lastReadTime).length;
  }, [entries, lastReadTime]);

  const handleToggle = useCallback(() => {
    setIsOpen((current) => !current);
  }, []);

  const handleNotificationClick = useCallback(
    (entry) => {
      markAllAsRead(entries);
      setIsOpen(false);
      navigate("/dashboard", {
        state: {
          focusOrderNumber: entry.orderNumber || "",
          focusToken: Date.now(),
        },
      });
    },
    [entries, markAllAsRead, navigate]
  );

  return (
    <div className="notification-shell" ref={containerRef}>
      <button
        type="button"
        className={`notification-trigger${isOpen ? " open" : ""}`}
        aria-label="Notifications"
        aria-expanded={isOpen}
        aria-haspopup="dialog"
        onClick={handleToggle}
      >
        <BellIcon />
        {unreadCount > 0 && <span className="notification-badge">{unreadCount > 99 ? "99+" : unreadCount}</span>}
      </button>

      <div className={`notification-dropdown${isOpen ? " visible" : ""}`} role="dialog" aria-label="Notifications">
        <div className="notification-dropdown-header">
          <div>
            <strong>Notifications</strong>
            <p>Recent order changes</p>
          </div>
          {unreadCount > 0 && <span className="status-chip danger">{unreadCount} unread</span>}
        </div>

        <div className="notification-dropdown-body">
          {loading && <p className="notification-state">Loading notifications...</p>}
          {!loading && error && <p className="notification-state error">Failed to load notifications</p>}
          {!loading && !error && entries.length === 0 && <p className="notification-state">No new notifications</p>}

          {!loading && !error && entries.length > 0 && (
            <div className="notification-list">
              {entries.map((entry) => {
                const isUnread = getEntryTimestamp(entry) > lastReadTime;

                return (
                  <button
                    type="button"
                    key={entry.id}
                    className={`notification-item${isUnread ? " unread" : ""}`}
                    onClick={() => handleNotificationClick(entry)}
                  >
                    <p>{entry.message}</p>
                    <small title={formatDateTime(entry.createdAt)}>{formatRelativeTime(entry.createdAt)}</small>
                  </button>
                );
              })}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

export default NotificationDropdown;