import { useState, useEffect } from 'react';
import ChatWindow from './components/ChatWindow';

interface Shipment {
  id: number;
  awbNumber: string;
  productType: string;
  productName?: string;
  quantityKg?: number;
  temperatureRequiredC?: number;
  originAirport?: string;
  destinationAirport?: string;
  status: string;
  createdAt: string;
}

interface Customer {
  id: number;
  fullName: string;
  email?: string;
  companyName?: string;
  country?: string;
}

export default function App() {
  const [shipments, setShipments] = useState<Shipment[]>([]);
  const [customers, setCustomers] = useState<Customer[]>([]);

  useEffect(() => {
    fetchData();
    const interval = setInterval(fetchData, 5000);
    return () => clearInterval(interval);
  }, []);

  async function fetchData() {
    try {
      const [sRes, cRes] = await Promise.all([
        fetch('/api/shipments'),
        fetch('/api/customers')
      ]);
      if (sRes.ok) setShipments(await sRes.json());
      if (cRes.ok) setCustomers(await cRes.json());
    } catch {
      // silencioso en dev
    }
  }

  return (
    <div className="app-container">
      <header className="app-header">
        <div className="brand-group">
          <div className="brand-mark">O</div>
          <div className="brand-copy">
            <div className="brand-eyebrow">Copilot operativo</div>
            <h1>Code Name Orion</h1>
            <p>AWB, clientes y cadena de frio en una sola vista clara y rapida.</p>
          </div>
        </div>
        <div className="status-stack">
          <div className="status-badge">Perishable Cargo Copilot</div>
          <div className="status-hint">UI simple, amigable y lista para operar</div>
        </div>
      </header>
      <main>
        <ChatWindow onRefresh={fetchData} />
        <aside className="sidebar">
          <div className="panel">
            <div className="panel-header">AWBs Recientes</div>
            <div className="data-list">
              {shipments.length === 0 && <div className="empty-state">Sin envíos registrados</div>}
              {shipments.map(s => (
                <div key={s.id} className="data-item">
                  <strong>{s.awbNumber}</strong>
                  <small>{s.productType} • {s.quantityKg}kg • {s.status}</small>
                  <small>{s.originAirport} → {s.destinationAirport} | {s.temperatureRequiredC}°C</small>
                </div>
              ))}
            </div>
          </div>
          <div className="panel">
            <div className="panel-header">Clientes</div>
            <div className="data-list">
              {customers.length === 0 && <div className="empty-state">Sin clientes registrados</div>}
              {customers.map(c => (
                <div key={c.id} className="data-item">
                  <strong>{c.fullName}</strong>
                  <small>{c.companyName} • {c.country}</small>
                  <small>{c.email}</small>
                </div>
              ))}
            </div>
          </div>
        </aside>
      </main>
    </div>
  );
}
