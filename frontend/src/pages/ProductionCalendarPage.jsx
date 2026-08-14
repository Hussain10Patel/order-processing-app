import { useEffect, useMemo, useState } from "react";
import StatusBlock from "../components/StatusBlock";
import { getProductionPlans } from "../services/api";

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

function formatCurrency(value) {
  const numeric = Number(value ?? 0);
  if (!Number.isFinite(numeric)) return "0";
  return numeric.toLocaleString(undefined, {
    minimumFractionDigits: 0,
    maximumFractionDigits: 2,
  });
}

function ProductionCalendarPage() {
  const [selectedDate, setSelectedDate] = useState(toLocalYMD(new Date()));
  const [plansByDate, setPlansByDate] = useState({});
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");

  const monthStart = useMemo(() => getMonthStart(selectedDate), [selectedDate]);
  const calendarDays = useMemo(() => getCalendarDays(selectedDate), [selectedDate]);

  useEffect(() => {
    async function loadMonthPlans() {
      setLoading(true);
      setError("");

      try {
        const month = monthStart.getMonth();
        const year = monthStart.getFullYear();
        const lastDay = new Date(year, month + 1, 0).getDate();
        const dates = Array.from({ length: lastDay }, (_, index) => {
          const date = new Date(year, month, index + 1);
          return toLocalYMD(date);
        });

        const results = await Promise.all(
          dates.map(async (date) => {
            try {
              const rows = await getProductionPlans(date);
              return { date, rows: Array.isArray(rows) ? rows : [] };
            } catch {
              return { date, rows: [] };
            }
          })
        );

        const nextPlans = {};
        results.forEach(({ date, rows }) => {
          nextPlans[date] = rows;
        });
        setPlansByDate(nextPlans);
      } catch (requestError) {
        setPlansByDate({});
        setError(requestError.message || "Unable to load production calendar");
      } finally {
        setLoading(false);
      }
    }

    void loadMonthPlans();
  }, [monthStart]);

  const selectedPlans = plansByDate[selectedDate] || [];

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
        <p>Daily production plan view using the existing ProductionPlan source of truth.</p>
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
          empty={!loading && !error && Object.keys(plansByDate).length === 0}
          loadingText="Loading production plan calendar..."
          emptyText="No production plan data found for this month"
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
                const dayPlans = dateKey ? plansByDate[dateKey] || [] : [];
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
                          {dayPlans.length > 0 && (
                            <span className="badge green" style={{ fontSize: 10 }}>
                              {dayPlans.length} item{dayPlans.length > 1 ? "s" : ""}
                            </span>
                          )}
                        </div>

                        {dayPlans.length > 0 ? (
                          <>
                            <div style={{ fontSize: 12, fontWeight: 600, marginBottom: 4 }}>
                              {formatCurrency(dayPlans.reduce((sum, plan) => sum + Number(plan.productionQuantity ?? 0), 0))} qty
                            </div>
                            {dayPlans.slice(0, 2).map((plan) => (
                              <div key={`${dateKey}-${plan.productId}`} style={{ fontSize: 11, marginBottom: 2 }}>
                                {plan.productName || "Product"}: {formatCurrency(plan.productionQuantity || 0)}
                              </div>
                            ))}
                            {dayPlans.length > 2 && (
                              <div style={{ fontSize: 11, color: "#475569" }}>+{dayPlans.length - 2} more</div>
                            )}
                          </>
                        ) : (
                          <div style={{ color: "#6b7280", fontSize: 11 }}>No plan</div>
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
              <h3 style={{ margin: 0 }}>Production plan for {formatShortDate(selectedDate)}</h3>
            </div>

            {selectedPlans.length === 0 ? (
              <p className="status-text">No production plan rows exist for this date.</p>
            ) : (
              <div style={{ overflowX: "auto" }}>
                <table style={{ width: "100%", borderCollapse: "collapse" }}>
                  <thead>
                    <tr>
                      <th style={{ textAlign: "left", padding: "8px 6px" }}>Product</th>
                      <th style={{ textAlign: "right", padding: "8px 6px" }}>Opening Stock</th>
                      <th style={{ textAlign: "right", padding: "8px 6px" }}>Production Qty</th>
                      <th style={{ textAlign: "right", padding: "8px 6px" }}>Total Demand</th>
                      <th style={{ textAlign: "right", padding: "8px 6px" }}>Closing Stock</th>
                      <th style={{ textAlign: "left", padding: "8px 6px" }}>Status</th>
                    </tr>
                  </thead>
                  <tbody>
                    {selectedPlans.map((plan) => {
                      const shortfall = Number(plan.closingStock ?? 0) < 0;
                      return (
                        <tr key={`${plan.productId}-${plan.date}`}>
                          <td style={{ padding: "8px 6px", borderTop: "1px solid #eee" }}>{plan.productName || "Unknown"}</td>
                          <td style={{ padding: "8px 6px", borderTop: "1px solid #eee", textAlign: "right" }}>
                            {formatCurrency(plan.openingStock)}
                          </td>
                          <td style={{ padding: "8px 6px", borderTop: "1px solid #eee", textAlign: "right" }}>
                            {formatCurrency(plan.productionQuantity)}
                          </td>
                          <td style={{ padding: "8px 6px", borderTop: "1px solid #eee", textAlign: "right" }}>
                            {formatCurrency(plan.totalOrderDemand)}
                          </td>
                          <td
                            style={{
                              padding: "8px 6px",
                              borderTop: "1px solid #eee",
                              textAlign: "right",
                              color: shortfall ? "var(--danger-color, #b00020)" : "inherit",
                              fontWeight: shortfall ? 700 : 400,
                            }}
                          >
                            {formatCurrency(plan.closingStock)}
                          </td>
                          <td style={{ padding: "8px 6px", borderTop: "1px solid #eee" }}>
                            <span className={shortfall ? "badge orange" : "badge green"}>
                              {shortfall ? "Shortfall" : "OK"}
                            </span>
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        </>
      )}
    </section>
  );
}

export default ProductionCalendarPage;
