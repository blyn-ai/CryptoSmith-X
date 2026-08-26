/** Exchange connectivity row — dot, venue name in mono caps, latency. */
export interface VenueStatusProps {
  venues: { name: string; latency?: string; ok?: boolean }[];
}
