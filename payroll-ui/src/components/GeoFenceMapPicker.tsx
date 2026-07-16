import { useEffect, useRef, useState } from 'react'
import { AimOutlined, SearchOutlined } from '@ant-design/icons'
import { Alert, Button, Input, Space, Spin } from 'antd'
import {
  circle,
  divIcon,
  map as createMap,
  marker,
  tileLayer,
  type Circle as LeafletCircle,
  type Map as LeafletMap,
  type Marker as LeafletMarker
} from 'leaflet'
import 'leaflet/dist/leaflet.css'

type GeoSearchResult = {
  place_id: number
  display_name: string
  lat: string
  lon: string
  type?: string
}

const INDIA_CENTER: [number, number] = [22.9734, 78.6569]
const tileUrl = import.meta.env.VITE_MAP_TILE_URL || 'https://tile.openstreetmap.org/{z}/{x}/{y}.png'
const tileAttribution = import.meta.env.VITE_MAP_ATTRIBUTION || '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
const geocodeUrl = import.meta.env.VITE_GEOCODING_SEARCH_URL || 'https://nominatim.openstreetmap.org/search'
const geocodeCache = new Map<string, GeoSearchResult[]>()
const geocodeRequests = new Map<string, Promise<GeoSearchResult[]>>()
let geocodeQueue: Promise<void> = Promise.resolve()
let lastGeocodeRequestAt = 0

const pinIcon = divIcon({
  className: 'geo-map-pin',
  html: '<span aria-hidden="true"></span>',
  iconAnchor: [14, 36],
  iconSize: [28, 36]
})

export default function GeoFenceMapPicker({
  latitude,
  longitude,
  radiusMeters,
  searchHints,
  onChange
}: {
  latitude: number
  longitude: number
  radiusMeters: number
  searchHints: string[]
  onChange: (latitude: number, longitude: number) => void
}) {
  const hasCoordinates = validCoordinates(latitude, longitude)
  const [query, setQuery] = useState(searchHints[0] || '')
  const [results, setResults] = useState<GeoSearchResult[]>([])
  const [searching, setSearching] = useState(false)
  const [locating, setLocating] = useState(false)
  const [message, setMessage] = useState('')
  const mapHostRef = useRef<HTMLDivElement | null>(null)
  const mapRef = useRef<LeafletMap | null>(null)
  const markerRef = useRef<LeafletMarker | null>(null)
  const circleRef = useRef<LeafletCircle | null>(null)
  const onChangeRef = useRef(onChange)

  useEffect(() => {
    onChangeRef.current = onChange
  }, [onChange])

  useEffect(() => {
    if (!mapHostRef.current || mapRef.current) return
    const initialCenter: [number, number] = hasCoordinates ? [latitude, longitude] : INDIA_CENTER
    const map = createMap(mapHostRef.current, { center: initialCenter, zoom: hasCoordinates ? 17 : 5, scrollWheelZoom: true })
    tileLayer(tileUrl, { attribution: tileAttribution }).addTo(map)
    map.on('click', event => onChangeRef.current(Number(event.latlng.lat.toFixed(7)), Number(event.latlng.lng.toFixed(7))))
    mapRef.current = map
    window.setTimeout(() => map.invalidateSize(), 0)
    return () => {
      markerRef.current = null
      circleRef.current = null
      mapRef.current = null
      map.remove()
    }
    // The picker is keyed by scope/location, so its initial viewport is intentionally captured once.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useEffect(() => {
    const map = mapRef.current
    if (!map) return
    if (!hasCoordinates) {
      if (markerRef.current) map.removeLayer(markerRef.current)
      if (circleRef.current) map.removeLayer(circleRef.current)
      markerRef.current = null
      circleRef.current = null
      return
    }
    const position: [number, number] = [latitude, longitude]
    if (!markerRef.current) {
      markerRef.current = marker(position, { icon: pinIcon, draggable: true })
        .on('dragend', event => {
          const draggedMarker = event.target as LeafletMarker
          const next = draggedMarker.getLatLng()
          onChangeRef.current(Number(next.lat.toFixed(7)), Number(next.lng.toFixed(7)))
        })
        .addTo(map)
    } else {
      markerRef.current.setLatLng(position)
    }
    if (!circleRef.current) {
      circleRef.current = circle(position, { radius: Math.max(25, radiusMeters), color: '#6546e8', fillColor: '#8b75ef', fillOpacity: 0.18, weight: 2 }).addTo(map)
    } else {
      circleRef.current.setLatLng(position).setRadius(Math.max(25, radiusMeters))
    }
    map.setView(position, 17, { animate: true })
  }, [hasCoordinates, latitude, longitude, radiusMeters])

  async function runLocationSearch(candidates: string[]) {
    const cleanCandidates = Array.from(new Set(candidates.map(candidate => candidate.trim()).filter(Boolean)))
    if (!cleanCandidates.length) return
    setSearching(true)
    setMessage('')
    setResults([])
    try {
      for (let index = 0; index < cleanCandidates.length; index += 1) {
        const candidate = cleanCandidates[index]
        const rows = await searchPlaces(candidate)
        if (!rows.length) continue
        const first = rows[0]
        setQuery(candidate)
        setResults(rows)
        mapRef.current?.setView([Number(first.lat), Number(first.lon)], index === 0 ? 13 : 11, { animate: true })
        if (index > 0) {
          setMessage(`The exact office was not found in OpenStreetMap. The map is centred using "${candidate}". Choose a result or click the exact office position.`)
        }
        return
      }
      setMessage('No matching place was found. Search a nearby landmark, use current location, or click the exact office position on the map.')
    } catch {
      setMessage('Map search is temporarily unavailable. You can still click the map or enter coordinates manually.')
    } finally {
      setSearching(false)
    }
  }

  // A work-location selection is a deliberate user action; lookups only recentre the map.
  useEffect(() => {
    if (!searchHints.length || hasCoordinates) return
    const timer = window.setTimeout(() => void runLocationSearch(searchHints), 0)
    return () => window.clearTimeout(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [searchHints.join('|')])

  async function runSearch(rawQuery = query, centerOnly = false) {
    const cleanQuery = rawQuery.trim()
    if (!cleanQuery) {
      setMessage('Enter an office, landmark, address, or city to search.')
      return
    }
    setSearching(true)
    setMessage('')
    try {
      const rows = await searchPlaces(cleanQuery)
      setResults(rows)
      if (rows.length === 0) {
        setMessage('No matching place was found. Try a nearby landmark or a more complete address.')
        return
      }
      const first = rows[0]
      const nextLatitude = Number(first.lat)
      const nextLongitude = Number(first.lon)
      mapRef.current?.setView([nextLatitude, nextLongitude], centerOnly ? 13 : 17, { animate: true })
      if (!centerOnly && rows.length === 1) onChange(nextLatitude, nextLongitude)
    } catch {
      setMessage('Map search is temporarily unavailable. You can still click the map or enter coordinates manually.')
    } finally {
      setSearching(false)
    }
  }

  function chooseResult(result: GeoSearchResult) {
    const nextLatitude = Number(result.lat)
    const nextLongitude = Number(result.lon)
    onChange(nextLatitude, nextLongitude)
    setResults([])
    setQuery(result.display_name)
    setMessage('')
  }

  function useCurrentLocation() {
    if (!navigator.geolocation) {
      setMessage('Browser location access is not available.')
      return
    }
    setLocating(true)
    setMessage('')
    navigator.geolocation.getCurrentPosition(
      position => {
        const nextLatitude = Number(position.coords.latitude.toFixed(7))
        const nextLongitude = Number(position.coords.longitude.toFixed(7))
        onChange(nextLatitude, nextLongitude)
        setLocating(false)
      },
      error => {
        setMessage(error.message || 'Unable to capture the current location.')
        setLocating(false)
      },
      { enableHighAccuracy: true, timeout: 15000, maximumAge: 0 }
    )
  }

  return <div className="geo-map-picker">
    <div className="geo-map-search">
      <Input
        value={query}
        placeholder="Search office, landmark, address, or city"
        onChange={event => setQuery(event.target.value)}
        onPressEnter={() => void runSearch()}
        prefix={<SearchOutlined />}
        allowClear
      />
      <Space>
        <Button loading={searching} onClick={() => void runSearch()}>Search map</Button>
        <Button icon={<AimOutlined />} loading={locating} onClick={useCurrentLocation}>Current location</Button>
      </Space>
    </div>

    {message && <Alert className="geo-map-alert" type="warning" showIcon message={message} />}
    {results.length > 0 && <div className="geo-map-results">
      {results.map(result => <button type="button" key={result.place_id} onClick={() => chooseResult(result)}>
        <strong>{result.type ? humanize(result.type) : 'Place'}</strong>
        <span>{result.display_name}</span>
      </button>)}
    </div>}

    <div className="geo-map-shell">
      {searching && <div className="geo-map-loading"><Spin /></div>}
      <div ref={mapHostRef} className="geo-fence-map" />
    </div>
    <small className="geo-map-help">Search a place, click anywhere on the map, or drag the pin. The shaded circle is the configured attendance radius.</small>
  </div>
}

async function searchPlaces(query: string): Promise<GeoSearchResult[]> {
  const key = query.trim().toLowerCase()
  const cached = geocodeCache.get(key)
  if (cached) return cached
  const pending = geocodeRequests.get(key)
  if (pending) return pending

  const request = queueGeocode(async () => {
    const url = new URL(geocodeUrl)
    url.searchParams.set('format', 'jsonv2')
    url.searchParams.set('limit', '5')
    url.searchParams.set('addressdetails', '1')
    url.searchParams.set('q', query)
    const response = await fetch(url, { headers: { Accept: 'application/json', 'Accept-Language': 'en' } })
    if (!response.ok) throw new Error(`Map search failed with ${response.status}`)
    const rows = await response.json() as GeoSearchResult[]
    geocodeCache.set(key, rows)
    return rows
  })
  geocodeRequests.set(key, request)
  try {
    return await request
  } finally {
    geocodeRequests.delete(key)
  }
}

function queueGeocode<T>(request: () => Promise<T>): Promise<T> {
  let resolveResult!: (value: T | PromiseLike<T>) => void
  let rejectResult!: (reason?: unknown) => void
  const result = new Promise<T>((resolve, reject) => {
    resolveResult = resolve
    rejectResult = reject
  })
  geocodeQueue = geocodeQueue.then(async () => {
    const waitMs = Math.max(0, 1000 - (Date.now() - lastGeocodeRequestAt))
    if (waitMs > 0) await new Promise(resolve => window.setTimeout(resolve, waitMs))
    lastGeocodeRequestAt = Date.now()
    try {
      resolveResult(await request())
    } catch (error) {
      rejectResult(error)
    }
  })
  return result
}

function validCoordinates(latitude: number, longitude: number) {
  return latitude >= -90 && latitude <= 90 && longitude >= -180 && longitude <= 180 && !(latitude === 0 && longitude === 0)
}

function humanize(value: string) {
  return value.replace(/_/g, ' ').replace(/\b\w/g, letter => letter.toUpperCase())
}
