import { QueryClientProvider } from '@tanstack/react-query'
import { createBrowserRouter, Navigate, RouterProvider } from 'react-router-dom'
import { createQueryClient } from './api/queries'
import { Layout } from './components/Layout'
import { EventExplorer } from './pages/EventExplorer'
import { EventDetailPage } from './pages/EventDetail'
import { ExportsPage } from './pages/Exports'
import { IntegrityPage } from './pages/IntegrityCheck'
import { RetentionPage } from './pages/Retention'
import { OverviewPage } from './pages/Overview'

const queryClient = createQueryClient()

const router = createBrowserRouter([
  {
    element: <Layout />,
    children: [
      { index: true, element: <Navigate to="/events" replace /> },
      { path: 'events', element: <EventExplorer /> },
      { path: 'events/:id', element: <EventDetailPage /> },
      { path: 'exports', element: <ExportsPage /> },
      { path: 'integrity', element: <IntegrityPage /> },
      { path: 'retention', element: <RetentionPage /> },
      { path: 'overview', element: <OverviewPage /> },
      { path: '*', element: <div className="empty-state"><strong>Page not found</strong><p className="muted">Use the menu on the left to find what you need.</p></div> },
    ],
  },
])

export function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  )
}
