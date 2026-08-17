import { useEffect, useMemo, useState } from "react";
import StatusBlock from "../components/StatusBlock";
import { getProductionCalendar } from "../services/api";

const weekdayLabels = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

function toLocalYMD(date) {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

function getMonthStart(dateInput) {
  const [year, month] = String(dateInput || toLocalYMD(new Date())).split("-").map(Number);
  return new Date(year, month - 1, 1);
}

function getCalendarDays(dateInput) {
  const monthStart = getMonthStart(dateInput);
  const month = monthStart.getMonth();
  const year = monthStart.getFullYear();
  const firstDay = new Date(year, month, 1);
  const lastDay = new Date(year, month + 1, 0);

  const startOffset = (firstDay.getDay() + 6) % 7;
  const cells = [];

  for (let i = 0; i < startOffset; i += 1) {
    cells.push(null);
  }

  for (let day = 1; day <= lastDay.getDate(); day += 1) {
    cells.push(new Date(year, month, day));
  }

  while (cells.length % 7 !== 0) {
    cells.push(null);
  }

  return cells;
}

function formatFriendlyDate(value) {
  if (!value) return "-";
  const date = new Date(`${value}T00:00:00`);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleDateString(undefined, {
    weekday: "long",
    day: "2-digit",
    month: "long",
    year: "numeric",
  });
}

function formatNumber(value) {
  const numeric = Number(value ?? 0);
  if (!Number.isFinite(numeric)) return "0";
  return numeric.toLocaleString(undefined, {
    minimumFractionDigits: 0,
    maximumFractionDigits: 2,
  });
}

function getDecisionLabel(item) {
  if (item.hasCurrentProductionCalculation === false) {
    return "No decision";
  }

  if (Number(item.currentRequiredProductionQty ?? 0) > 0) {
    return "Requires production";
  }

  return "No production required";
}

function getWeekDates(dateInput) {
  const baseDate = new Date(`${dateInput || toLocalYMD(new Date())}T00:00:00`);
  const dayIndex = (baseDate.getDay() + 6) % 7;
  const monday = new Date(baseDate);
  monday.setDate(baseDate.getDate() - dayIndex);

  return Array.from({ length: 7 }, (_, index) => {
    const nextDate = new Date(monday);
    nextDate.setDate(monday.getDate() + index);
    return toLocalYMD(nextDate);
  });
}

function ProductionCalendarPage() {
  const [selectedDate, setSelectedDate] = useState(toLocalYMD(new Date()));
  const [calendarView, setCalendarView] = useState("month");
  const [calendarByDate, setCalendarByDate] = useState({});
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");

  const monthStart = useMemo(() => getMonthStart(selectedDate), [selectedDate]);
  const calendarDays = useMemo(() => getCalendarDays(selectedDate), [selectedDate]);

  useEffect(() => {
    async function loadMonthCalendar() {
      setLoading(true);
      setError("");

      try {
        const month = monthStart.getMonth();
        const year = monthStart.getFullYear();
        const firstDay = new Date(year, month, 1);
        const lastDay = new Date(year, month + 1, 0);
        const fromDate = toLocalYMD(firstDay);
        const toDate = toLocalYMD(lastDay);

        const rows = await getProductionCalendar(fromDate, toDate);
        const nextByDate = {};
        (rows || []).forEach((day) => {
          nextByDate[day.date] = day;
        });
        setCalendarByDate(nextByDate);
      } catch (requestError) {
        setCalendarByDate({});
        setError(requestError.message || "Unable to load production calendar");
      } finally {
        setLoading(false);
      }
    }

    void loadMonthCalendar();
  }, [monthStart]);

  const selectedDay = calendarByDate[selectedDate] || { scheduledItems: [], unscheduledItems: [] };
  const selectedItems = [...selectedDay.scheduledItems, ...selectedDay.unscheduledItems];

  const monthLabel = monthStart.toLocaleDateString(undefined, {
    month: "long",
    year: "numeric",
  });
  const weekDates = useMemo(() => getWeekDates(selectedDate), [selectedDate]);

  function moveMonth(offset) {
    const nextMonth = new Date(monthStart.getFullYear(), monthStart.getMonth() + offset, 1);
    setSelectedDate(toLocalYMD(nextMonth));
  }

  function moveSelectedDate(offset) {
    const nextDate = new Date(`${selectedDate}T00:00:00`);
    nextDate.setDate(nextDate.getDate() + offset);
    setSelectedDate(toLocalYMD(nextDate));
  }

  function moveView(offset) {
    if (calendarView === "week") {
      moveSelectedDate(offset * 7);
      return;
    }

    if (calendarView === "day") {
      moveSelectedDate(offset);
      return;
    }

    moveMonth(offset);
  }

  function handleCellClick(dateValue) {
    if (!dateValue) {
      return;
    }
    setSelectedDate(dateValue);
  }

  return (
    <section className="production-calendar-page">
      <header className="page-header production-calendar-header">
        <div>
          <h2>Production Calendar</h2>
          <p>Daily view of scheduled and unscheduled production work.</p>
        </div>
      </header>

      <div className="panel production-calendar-top-panel">
        <div className="production-calendar-toolbar">
          <div className="calendar-toolbar-group left-group">
            <button type="button" className="secondary calendar-nav-button" onClick={() => moveView(-1)} aria-label={calendarView === "month" ? "Previous month" : calendarView === "week" ? "Previous week" : "Previous day"}>
              ‹
            </button>
            <button type="button" className="secondary calendar-nav-button" onClick={() => moveView(1)} aria-label={calendarView === "month" ? "Next month" : calendarView === "week" ? "Next week" : "Next day"}>
              ›
            </button>
            <button type="button" className="secondary" onClick={() => setSelectedDate(toLocalYMD(new Date()))}>Today</button>
          </div>

          <div className="calendar-toolbar-title">
            <span className="calendar-icon" aria-hidden="true">📅</span>
            <strong>{calendarView === "month" ? monthLabel : calendarView === "week" ? `${new Date(`${weekDates[0]}T00:00:00`).toLocaleDateString(undefined, { month: "short", day: "numeric" })} - ${new Date(`${weekDates[6]}T00:00:00`).toLocaleDateString(undefined, { month: "short", day: "numeric" })}` : new Date(`${selectedDate}T00:00:00`).toLocaleDateString(undefined, { weekday: "long", month: "long", day: "numeric", year: "numeric" })}</strong>
          </div>

          <div className="calendar-toolbar-input">
            <label htmlFor="production-calendar-date">Selected date</label>
            <input
              id="production-calendar-date"
              type="date"
              value={selectedDate}
              onChange={(event) => setSelectedDate(event.target.value || toLocalYMD(new Date()))}
            />
          </div>
        </div>

        <div className="calendar-view-switcher" aria-label="Calendar view selector">
          <button
            type="button"
            className={calendarView === "month" ? "calendar-view-button active" : "calendar-view-button"}
            onClick={() => setCalendarView("month")}
            aria-pressed={calendarView === "month"}
          >
            Month
          </button>
          <button
            type="button"
            className={calendarView === "week" ? "calendar-view-button active" : "calendar-view-button"}
            onClick={() => setCalendarView("week")}
            aria-pressed={calendarView === "week"}
          >
            Week
          </button>
          <button
            type="button"
            className={calendarView === "day" ? "calendar-view-button active" : "calendar-view-button"}
            onClick={() => setCalendarView("day")}
            aria-pressed={calendarView === "day"}
          >
            Day
          </button>
        </div>

        <div className="calendar-legend" aria-label="Production status legend">
          <span className="legend-item"><span className="legend-swatch scheduled-swatch" />Scheduled</span>
          <span className="legend-item"><span className="legend-swatch unscheduled-swatch" />Unscheduled</span>
          <span className="legend-item"><span className="legend-swatch empty-swatch" />No production work</span>
        </div>
      </div>

      <StatusBlock
        loading={loading}
        error={error}
        empty={!loading && !error && Object.keys(calendarByDate).length === 0}
        loadingText="Loading production calendar..."
        emptyText="No production work found for this month"
        spinner
      />

      {!loading && !error && (
        <>
          {calendarView === "month" && (
            <div className="panel production-calendar-month-panel">
              <div className="calendar-weekdays">
                {weekdayLabels.map((label) => (
                  <div key={label} className="calendar-weekday">
                    {label}
                  </div>
                ))}
              </div>

              <div className="calendar-month-grid">
                {calendarDays.map((dateValue, index) => {
                  const dateKey = dateValue ? toLocalYMD(dateValue) : "";
                  const day = dateKey ? calendarByDate[dateKey] : null;
                  const scheduledCount = day ? day.scheduledItems.length : 0;
                  const unscheduledCount = day ? day.unscheduledItems.length : 0;
                  const totalCount = scheduledCount + unscheduledCount;
                  const isSelected = dateKey === selectedDate;
                  const isMuted = dateValue && dateValue.getMonth() !== monthStart.getMonth();

                  return (
                    <button
                      key={dateKey || `empty-${index}`}
                      type="button"
                      onClick={() => handleCellClick(dateKey)}
                      className={[
                        "calendar-day-cell",
                        dateValue ? "has-date" : "empty-cell",
                        isSelected ? "selected" : "",
                        isMuted ? "muted" : "",
                        totalCount > 0 ? "has-work" : "",
                      ].filter(Boolean).join(" ")}
                    >
                      {dateValue ? (
                        <>
                          <div className="calendar-day-header">
                            <span className="calendar-day-number">{dateValue.getDate()}</span>
                            {totalCount > 0 && <span className="calendar-badge">{totalCount}</span>}
                          </div>

                          {totalCount > 0 ? (
                            <div className="calendar-day-body">
                              {scheduledCount > 0 && (
                                <div className="calendar-day-stat scheduled-stat">
                                  {scheduledCount} scheduled
                                </div>
                              )}
                              {unscheduledCount > 0 && (
                                <div className="calendar-day-stat unscheduled-stat">
                                  {unscheduledCount} unscheduled
                                </div>
                              )}

                              <div className="calendar-day-preview">
                                {day && day.scheduledItems.slice(0, 1).map((item) => (
                                  <div key={`${dateKey}-scheduled-${item.orderId}-${item.orderItemId}`} className="calendar-preview-item">
                                    {item.orderNumber}: {item.productName}
                                  </div>
                                ))}
                                {day && day.unscheduledItems.slice(0, 1).map((item) => (
                                  <div key={`${dateKey}-unscheduled-${item.orderId}-${item.orderItemId}`} className="calendar-preview-item">
                                    {item.orderNumber}: {item.productName}
                                  </div>
                                ))}
                              </div>
                            </div>
                          ) : (
                            <div className="calendar-day-empty">No production work</div>
                          )}
                        </>
                      ) : null}
                    </button>
                  );
                })}
              </div>
            </div>
          )}

          {calendarView === "week" && (
            <div className="panel production-calendar-week-panel">
              <div className="calendar-week-grid">
                {weekDates.map((dateKey) => {
                  const day = calendarByDate[dateKey] || { scheduledItems: [], unscheduledItems: [] };
                  const scheduledCount = day.scheduledItems.length;
                  const unscheduledCount = day.unscheduledItems.length;
                  const totalCount = scheduledCount + unscheduledCount;
                  const dateValue = new Date(`${dateKey}T00:00:00`);
                  const isSelected = dateKey === selectedDate;

                  return (
                    <button
                      key={dateKey}
                      type="button"
                      onClick={() => setSelectedDate(dateKey)}
                      className={[
                        "calendar-week-day-card",
                        isSelected ? "selected" : "",
                        totalCount > 0 ? "has-work" : "",
                      ].filter(Boolean).join(" ")}
                    >
                      <div className="calendar-week-day-header">
                        <span className="calendar-weekday-name">{weekdayLabels[(dateValue.getDay() + 6) % 7]}</span>
                        <span className="calendar-week-date">{dateValue.getDate()}</span>
                      </div>

                      {totalCount > 0 ? (
                        <>
                          {scheduledCount > 0 && (
                            <div className="calendar-week-stat scheduled-stat">{scheduledCount} scheduled</div>
                          )}
                          {unscheduledCount > 0 && (
                            <div className="calendar-week-stat unscheduled-stat">{unscheduledCount} unscheduled</div>
                          )}
                          <div className="calendar-week-preview">
                            {day.scheduledItems.slice(0, 1).map((item) => (
                              <div key={`${dateKey}-scheduled-${item.orderId}-${item.orderItemId}`} className="calendar-preview-item">
                                {item.orderNumber}: {item.productName}
                              </div>
                            ))}
                            {day.unscheduledItems.slice(0, 1).map((item) => (
                              <div key={`${dateKey}-unscheduled-${item.orderId}-${item.orderItemId}`} className="calendar-preview-item">
                                {item.orderNumber}: {item.productName}
                              </div>
                            ))}
                          </div>
                        </>
                      ) : (
                        <div className="calendar-day-empty">No production work</div>
                      )}
                    </button>
                  );
                })}
              </div>
            </div>
          )}

          {calendarView === "day" && (
            <div className="panel production-calendar-day-panel">
              <div className="calendar-day-hero">
                <div>
                  <div className="calendar-day-hero-label">Selected date</div>
                  <h3>{formatFriendlyDate(selectedDate)}</h3>
                </div>
              </div>

              {selectedItems.length === 0 ? (
                <p className="status-text">No production work</p>
              ) : (
                <div className="production-detail-groups">
                  {selectedDay.scheduledItems.length > 0 && (
                    <div className="production-detail-group scheduled-group">
                      <h4>Scheduled ({selectedDay.scheduledItems.length})</h4>
                      <div className="production-items-list">
                        {selectedDay.scheduledItems.map((item) => (
                          <article key={`scheduled-${item.orderId}-${item.orderItemId}`} className="production-item-card scheduled-card">
                            <div className="production-item-main">
                              <div className="production-item-order">{item.orderNumber}</div>
                              <span className="status-chip success">{item.status}</span>
                            </div>
                            <div className="production-item-product">{item.productName}</div>
                            <div className="production-item-meta">
                              <span>{formatNumber(item.quantity)} pcs</span>
                              {item.distributionCentreName && <span>{item.distributionCentreName}</span>}
                            </div>
                            {item.hasCurrentProductionCalculation && (
                              <div className="production-item-note">Required production: {formatNumber(item.currentRequiredProductionQty)}</div>
                            )}
                            {(item.hasCurrentProductionCalculation || item.decisionIsSufficient !== null && item.decisionIsSufficient !== undefined) && (
                              <div className="production-item-note">
                                Production decision: {getDecisionLabel(item)}
                              </div>
                            )}
                          </article>
                        ))}
                      </div>
                    </div>
                  )}

                  {selectedDay.unscheduledItems.length > 0 && (
                    <div className="production-detail-group unscheduled-group">
                      <h4>Unscheduled ({selectedDay.unscheduledItems.length})</h4>
                      <div className="production-items-list">
                        {selectedDay.unscheduledItems.map((item) => (
                          <article key={`unscheduled-${item.orderId}-${item.orderItemId}`} className="production-item-card unscheduled-card">
                            <div className="production-item-main">
                              <div className="production-item-order">{item.orderNumber}</div>
                              <span className="status-chip warning">{item.status}</span>
                            </div>
                            <div className="production-item-product">{item.productName}</div>
                            <div className="production-item-meta">
                              <span>{formatNumber(item.quantity)} pcs</span>
                              {item.distributionCentreName && <span>{item.distributionCentreName}</span>}
                            </div>
                            {item.hasCurrentProductionCalculation && (
                              <div className="production-item-note">Required production: {formatNumber(item.currentRequiredProductionQty)}</div>
                            )}
                            {(item.hasCurrentProductionCalculation || item.decisionIsSufficient !== null && item.decisionIsSufficient !== undefined) && (
                              <div className="production-item-note">
                                Production decision: {getDecisionLabel(item)}
                              </div>
                            )}
                          </article>
                        ))}
                      </div>
                    </div>
                  )}
                </div>
              )}
            </div>
          )}

          <div className="panel production-calendar-detail-panel">
            <div className="production-detail-header">
              <h3>{formatFriendlyDate(selectedDate)}</h3>
            </div>

            {selectedItems.length === 0 ? (
              <p className="status-text">No production work</p>
            ) : (
              <div className="production-detail-groups">
                {selectedDay.scheduledItems.length > 0 && (
                  <div className="production-detail-group scheduled-group">
                    <h4>Scheduled ({selectedDay.scheduledItems.length})</h4>
                    <div className="production-items-list">
                      {selectedDay.scheduledItems.map((item) => (
                        <article key={`scheduled-${item.orderId}-${item.orderItemId}`} className="production-item-card scheduled-card">
                          <div className="production-item-main">
                            <div className="production-item-order">{item.orderNumber}</div>
                            <span className="status-chip success">{item.status}</span>
                          </div>
                          <div className="production-item-product">{item.productName}</div>
                          <div className="production-item-meta">
                            <span>{formatNumber(item.quantity)} pcs</span>
                            {item.distributionCentreName && <span>{item.distributionCentreName}</span>}
                          </div>
                          {item.hasCurrentProductionCalculation && (
                            <div className="production-item-note">Required production: {formatNumber(item.currentRequiredProductionQty)}</div>
                          )}
                          {(item.hasCurrentProductionCalculation || item.decisionIsSufficient !== null && item.decisionIsSufficient !== undefined) && (
                            <div className="production-item-note">
                              Production decision: {getDecisionLabel(item)}
                            </div>
                          )}
                        </article>
                      ))}
                    </div>
                  </div>
                )}

                {selectedDay.unscheduledItems.length > 0 && (
                  <div className="production-detail-group unscheduled-group">
                    <h4>Unscheduled ({selectedDay.unscheduledItems.length})</h4>
                    <div className="production-items-list">
                      {selectedDay.unscheduledItems.map((item) => (
                        <article key={`unscheduled-${item.orderId}-${item.orderItemId}`} className="production-item-card unscheduled-card">
                          <div className="production-item-main">
                            <div className="production-item-order">{item.orderNumber}</div>
                            <span className="status-chip warning">{item.status}</span>
                          </div>
                          <div className="production-item-product">{item.productName}</div>
                          <div className="production-item-meta">
                            <span>{formatNumber(item.quantity)} pcs</span>
                            {item.distributionCentreName && <span>{item.distributionCentreName}</span>}
                          </div>
                          {item.hasCurrentProductionCalculation && (
                            <div className="production-item-note">Required production: {formatNumber(item.currentRequiredProductionQty)}</div>
                          )}
                          {(item.hasCurrentProductionCalculation || item.decisionIsSufficient !== null && item.decisionIsSufficient !== undefined) && (
                            <div className="production-item-note">
                              Production decision: {getDecisionLabel(item)}
                            </div>
                          )}
                        </article>
                      ))}
                    </div>
                  </div>
                )}
              </div>
            )}
          </div>
        </>
      )}
    </section>
  );
}

export default ProductionCalendarPage;
