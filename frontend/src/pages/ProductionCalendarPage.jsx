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

function formatShortDate(value) {
  if (!value) return "-";
  const date = new Date(`${value}T00:00:00`);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleDateString(undefined, { month: "short", day: "numeric" });
}

function formatNumber(value) {
  const numeric = Number(value ?? 0);
  if (!Number.isFinite(numeric)) return "0";
  return numeric.toLocaleString(undefined, {
    minimumFractionDigits: 0,
    maximumFractionDigits: 2,
  });
}

function ProductionCalendarPage() {
  const [selectedDate, setSelectedDate] = useState(toLocalYMD(new Date()));
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

  function moveMonth(offset) {
    const nextMonth = new Date(monthStart.getFullYear(), monthStart.getMonth() + offset, 1);
    setSelectedDate(toLocalYMD(nextMonth));
  }

  function handleCellClick(dateValue) {
    if (!dateValue) {
      return;
    }
    setSelectedDate(dateValue);
  }

  return (
    <section>
      <header className="page-header">
        <h2>Production Calendar</h2>
        <p>Daily production work grouped by scheduled and unscheduled items using the existing workflow.</p>
      </header>

      <div className="panel" style={{ marginBottom: 16 }}>
        <div className="section-heading" style={{ gap: 12, flexWrap: "wrap" }}>
          <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
            <button type="button" onClick={() => moveMonth(-1)} className="secondary">Prev</button>
            <button type="button" onClick={() => moveMonth(1)} className="secondary">Next</button>
          </div>

          <div>
            <strong>{monthLabel}</strong>
          </div>

          <div style={{ minWidth: 220 }}>
            <label htmlFor="production-calendar-date">Selected date</label>
            <input
              id="production-calendar-date"
              type="date"
              value={selectedDate}
              onChange={(event) => setSelectedDate(event.target.value || toLocalYMD(new Date()))}
            />
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
      </div>

      {!loading && !error && (
        <>
          <div className="panel" style={{ marginBottom: 16 }}>
            <div
              style={{
                display: "grid",
                gridTemplateColumns: "repeat(7, minmax(120px, 1fr))",
                gap: 8,
              }}
            >
              {weekdayLabels.map((label) => (
                <div key={label} style={{ fontWeight: 700, padding: "8px 10px", textAlign: "center" }}>
                  {label}
                </div>
              ))}

              {calendarDays.map((dateValue, index) => {
                const dateKey = dateValue ? toLocalYMD(dateValue) : "";
                const day = dateKey ? calendarByDate[dateKey] : null;
                const scheduledCount = day ? day.scheduledItems.length : 0;
                const unscheduledCount = day ? day.unscheduledItems.length : 0;
                const totalCount = scheduledCount + unscheduledCount;
                const isSelected = dateKey === selectedDate;
                const isCurrentMonth = dateValue && dateValue.getMonth() === monthStart.getMonth();

                return (
                  <button
                    key={dateKey || `empty-${index}`}
                    type="button"
                    onClick={() => handleCellClick(dateKey)}
                    style={{
                      border: isSelected ? "2px solid #1d4ed8" : "1px solid #d9dfe8",
                      borderRadius: 10,
                      minHeight: 120,
                      textAlign: "left",
                      padding: 10,
                      background: dateValue ? (isSelected ? "#eff6ff" : "#fff") : "#f8fafc",
                      opacity: dateValue ? 1 : 0.7,
                      cursor: dateValue ? "pointer" : "default",
                    }}
                  >
                    {dateValue ? (
                      <>
                        <div style={{ display: "flex", justifyContent: "space-between", marginBottom: 6 }}>
                          <span style={{ fontWeight: isCurrentMonth ? 700 : 500, color: isCurrentMonth ? "inherit" : "#6b7280" }}>
                            {dateValue.getDate()}
                          </span>
                          {totalCount > 0 && (
                            <span className="badge green" style={{ fontSize: 10 }}>
                              {totalCount} item{totalCount > 1 ? "s" : ""}
                            </span>
                          )}
                        </div>

                        {totalCount > 0 ? (
                          <>
                            {scheduledCount > 0 && (
                              <div style={{ fontSize: 11, marginBottom: 4, color: "#166534", fontWeight: 700 }}>
                                {scheduledCount} scheduled
                              </div>
                            )}
                            {unscheduledCount > 0 && (
                              <div style={{ fontSize: 11, marginBottom: 4, color: "#92400e", fontWeight: 700 }}>
                                {unscheduledCount} unscheduled
                              </div>
                            )}
                            {day && day.scheduledItems.slice(0, 2).map((item) => (
                              <div key={`${dateKey}-scheduled-${item.orderId}-${item.orderItemId}`} style={{ fontSize: 11, marginBottom: 2 }}>
                                {item.orderNumber}: {item.productName}
                              </div>
                            ))}
                            {day && day.unscheduledItems.slice(0, 1).map((item) => (
                              <div key={`${dateKey}-unscheduled-${item.orderId}-${item.orderItemId}`} style={{ fontSize: 11, marginBottom: 2 }}>
                                {item.orderNumber}: {item.productName}
                              </div>
                            ))}
                          </>
                        ) : (
                          <div style={{ color: "#6b7280", fontSize: 11 }}>No production work</div>
                        )}
                      </>
                    ) : null}
                  </button>
                );
              })}
            </div>
          </div>

          <div className="panel">
            <div className="section-heading" style={{ marginBottom: 12 }}>
              <h3 style={{ margin: 0 }}>Production work for {formatShortDate(selectedDate)}</h3>
            </div>

            {selectedItems.length === 0 ? (
              <p className="status-text">No production work for this date.</p>
            ) : (
              <div style={{ display: "grid", gap: 16 }}>
                {selectedDay.scheduledItems.length > 0 && (
                  <div>
                    <h4 style={{ margin: "0 0 8px" }}>Scheduled</h4>
                    <table style={{ width: "100%", borderCollapse: "collapse" }}>
                      <thead>
                        <tr>
                          <th style={{ textAlign: "left", padding: "8px 6px" }}>Order</th>
                          <th style={{ textAlign: "left", padding: "8px 6px" }}>Product</th>
                          <th style={{ textAlign: "right", padding: "8px 6px" }}>Quantity</th>
                          <th style={{ textAlign: "left", padding: "8px 6px" }}>DC</th>
                          <th style={{ textAlign: "left", padding: "8px 6px" }}>Status</th>
                        </tr>
                      </thead>
                      <tbody>
                        {selectedDay.scheduledItems.map((item) => (
                          <tr key={`scheduled-${item.orderId}-${item.orderItemId}`}>
                            <td style={{ padding: "8px 6px", borderTop: "1px solid #eee" }}>{item.orderNumber}</td>
                            <td style={{ padding: "8px 6px", borderTop: "1px solid #eee" }}>{item.productName}</td>
                            <td style={{ padding: "8px 6px", borderTop: "1px solid #eee", textAlign: "right" }}>{formatNumber(item.quantity)}</td>
                            <td style={{ padding: "8px 6px", borderTop: "1px solid #eee" }}>{item.distributionCentreName}</td>
                            <td style={{ padding: "8px 6px", borderTop: "1px solid #eee" }}>
                              <span className="badge green">{item.status}</span>
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}

                {selectedDay.unscheduledItems.length > 0 && (
                  <div>
                    <h4 style={{ margin: "0 0 8px" }}>Unscheduled</h4>
                    <table style={{ width: "100%", borderCollapse: "collapse" }}>
                      <thead>
                        <tr>
                          <th style={{ textAlign: "left", padding: "8px 6px" }}>Order</th>
                          <th style={{ textAlign: "left", padding: "8px 6px" }}>Product</th>
                          <th style={{ textAlign: "right", padding: "8px 6px" }}>Quantity</th>
                          <th style={{ textAlign: "left", padding: "8px 6px" }}>DC</th>
                          <th style={{ textAlign: "left", padding: "8px 6px" }}>Status</th>
                        </tr>
                      </thead>
                      <tbody>
                        {selectedDay.unscheduledItems.map((item) => (
                          <tr key={`unscheduled-${item.orderId}-${item.orderItemId}`}>
                            <td style={{ padding: "8px 6px", borderTop: "1px solid #eee" }}>{item.orderNumber}</td>
                            <td style={{ padding: "8px 6px", borderTop: "1px solid #eee" }}>{item.productName}</td>
                            <td style={{ padding: "8px 6px", borderTop: "1px solid #eee", textAlign: "right" }}>{formatNumber(item.quantity)}</td>
                            <td style={{ padding: "8px 6px", borderTop: "1px solid #eee" }}>{item.distributionCentreName}</td>
                            <td style={{ padding: "8px 6px", borderTop: "1px solid #eee" }}>
                              <span className="badge orange">{item.status}</span>
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
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
