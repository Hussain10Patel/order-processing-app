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
const MAX_NOTIFICATIONS_PER_CATEGORY = 25;
const CATEGORY_ORDER = ["Order Changes", "Scheduling", "Production", "Other"];

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

function getCategoryForEntry(entry) {
  const rawEntity = String(entry?.entity ?? "").trim();

  if (rawEntity === "Order") {
    return "Order Changes";
  }

  if (rawEntity === "Delivery") {
    return "Scheduling";
  }

  if (rawEntity === "ProductionDecision") {
    return "Production";
  }

  return "Other";
}

function buildNotificationGroups(entries) {
  const grouped = new Map();

  (Array.isArray(entries) ? entries : []).forEach((entry) => {
    const category = getCategoryForEntry(entry);
    if (!grouped.has(category)) {
      grouped.set(category, []);
    }

    grouped.get(category).push(entry);
  });

  const orderedCategories = [...CATEGORY_ORDER, ...Array.from(grouped.keys()).filter((category) => !CATEGORY_ORDER.includes(category))];

  return orderedCategories
    .filter((category) => grouped.has(category))
    .map((category) => ({
      category,
      entries: [...grouped.get(category)]
        .sort((left, right) => getEntryTimestamp(right) - getEntryTimestamp(left))
        .slice(0, MAX_NOTIFICATIONS_PER_CATEGORY),
    }))
    .filter((group) => group.entries.length > 0);
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
  const [activeCategory, setActiveCategory] = useState("Order Changes");
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
          .map((entry) => ({
            ...entry,
            category: getCategoryForEntry(entry),
            orderNumber: ordersById.get(entry.entityId)?.orderNumber ?? "",
            message: formatAuditEntry(entry, ordersById.get(entry.entityId)),
          }))
          .sort((left, right) => getEntryTimestamp(right) - getEntryTimestamp(left));

        const groupedEntries = buildNotificationGroups(nextEntries);
        const flattenedEntries = groupedEntries.flatMap((group) => group.entries);

        setEntries(flattenedEntries);
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

  const groupedEntries = useMemo(() => buildNotificationGroups(entries), [entries]);

  useEffect(() => {
    if (groupedEntries.length === 0) {
      return;
    }

    if (!groupedEntries.some((group) => group.category === activeCategory)) {
      setActiveCategory(groupedEntries[0].category);
    }
  }, [groupedEntries, activeCategory]);

  const categoryTabs = useMemo(
    () =>
      CATEGORY_ORDER.map((category) => {
        const group = groupedEntries.find((entryGroup) => entryGroup.category === category);

        return {
          category,
          entries: group?.entries ?? [],
          unreadCount: entries.filter(
            (entry) => getCategoryForEntry(entry) === category && getEntryTimestamp(entry) > lastReadTime
          ).length,
        };
      }),
    [entries, groupedEntries, lastReadTime]
  );

  const activeCategoryGroup = useMemo(
    () => categoryTabs.find((tab) => tab.category === activeCategory) ?? categoryTabs[0] ?? { category: "Order Changes", entries: [], unreadCount: 0 },
    [activeCategory, categoryTabs]
  );

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

        <div className="notification-tab-list" role="tablist" aria-label="Notification categories">
          {CATEGORY_ORDER.map((category) => {
            const tab = categoryTabs.find((item) => item.category === category) ?? { category, entries: [], unreadCount: 0 };
            const isActive = activeCategory === category;

            return (
              <button
                key={category}
                type="button"
                role="tab"
                aria-selected={isActive}
                className={`notification-tab${isActive ? " active" : ""}`}
                onClick={() => setActiveCategory(category)}
              >
                <span>{category}</span>
                {tab.unreadCount > 0 && <span className="notification-tab-badge">{tab.unreadCount}</span>}
              </button>
            );
          })}
        </div>

        <div className="notification-dropdown-body">
          {loading && <p className="notification-state">Loading notifications...</p>}
          {!loading && error && <p className="notification-state error">Failed to load notifications</p>}
          {!loading && !error && entries.length === 0 && <p className="notification-state">No new notifications</p>}

          {!loading && !error && entries.length > 0 && (
            <div className="notification-list">
              {activeCategoryGroup.entries.length > 0 ? (
                activeCategoryGroup.entries.map((entry) => {
                  const isUnread = getEntryTimestamp(entry) > lastReadTime;

                  return (
                    <button
                      type="button"
                      key={`${activeCategoryGroup.category}-${entry.id}`}
                      className={`notification-item${isUnread ? " unread" : ""}`}
                      onClick={() => handleNotificationClick(entry)}
                    >
                      <p>{entry.message}</p>
                      <small title={formatDateTime(entry.createdAt)}>{formatRelativeTime(entry.createdAt)}</small>
                    </button>
                  );
                })
              ) : (
                <p className="notification-state">No notifications</p>
              )}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

export default NotificationDropdown;